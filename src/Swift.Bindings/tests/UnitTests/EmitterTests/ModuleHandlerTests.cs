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

    #region Dependency Module Import Tests

    [Fact]
    public void EmitSwiftImports_ImportsDependencyModuleNames()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.DependencyModuleNames = new List<string> { "BlinkID" };
            });

        Assert.Contains("import BlinkID", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_DependencyModulesDoNotDuplicateSelf()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.DependencyModuleNames = new List<string> { "TestModule" };
            });

        // "import TestModule" should appear exactly once (from the standard module import, not the dependency)
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(swiftOutput, "import TestModule"));
    }

    [Fact]
    public void EmitSwiftImports_DependencyModulesDoNotDuplicateAppleFramework()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string> { "UIKit" },
            moduleDecl =>
            {
                moduleDecl.DependencyModuleNames = new List<string> { "UIKit" };
            });

        // UIKit is already imported via the Apple frameworks path — should appear only once
        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(swiftOutput, "import UIKit"));
    }

    #endregion

    #region EveryProtocol Unsupported Module Tests

    [Fact]
    public void EmitEveryProtocol_SkipsProtocolWithSwiftUIPropertyTypes()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            moduleDecl =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "ThemeProtocol",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ThemeProtocol"),
                    MangledName = "$s10TestModule13ThemeProtocolP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("primaryColor", "SwiftUI.Color", moduleDecl)
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                };
                moduleDecl.Protocols.Add(protocol);
            });

        // Should NOT contain vtable struct or extension for ThemeProtocol
        Assert.DoesNotContain("ThemeProtocol_vtable", swiftOutput);
        Assert.DoesNotContain("extension EveryProtocol: TestModule.ThemeProtocol", swiftOutput);
    }

    [Fact]
    public void EmitEveryProtocol_EmitsProtocolWithSupportedTypes()
    {
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            moduleDecl =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "CounterProtocol",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CounterProtocol"),
                    MangledName = "$s10TestModule15CounterProtocolP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("count", "Swift.Int", moduleDecl)
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                };
                moduleDecl.Protocols.Add(protocol);
            });

        // Should contain extension conformance for CounterProtocol
        Assert.Contains("extension EveryProtocol: TestModule.CounterProtocol", swiftOutput);
    }

    #endregion

    #region Protocol Proxy Emission Coupling Tests

    [Fact]
    public void Emit_SkipsProxyForProtocolWithSwiftUIMembers()
    {
        // Adds protocol to both Types (for C# emission via HandleBaseDecl) and Protocols (for Swift EveryProtocol).
        // Verifies that the C# interface IS emitted but the proxy class is NOT.
        var (csOutput, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            moduleDecl =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "ThemeProtocol",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.ThemeProtocol"),
                    MangledName = "$s10TestModule13ThemeProtocolP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("primaryColor", "SwiftUI.Color", moduleDecl)
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                };
                moduleDecl.Protocols.Add(protocol);
                moduleDecl.Types.Add(protocol);
            });

        // C# interface should be emitted
        Assert.Contains("public interface IThemeProtocol", csOutput);

        // C# proxy class should NOT be emitted (would reference non-existent Swift symbols)
        Assert.DoesNotContain("ThemeProtocolProxy", csOutput);

        // Swift EveryProtocol conformance should NOT be emitted
        Assert.DoesNotContain("ThemeProtocol_vtable", swiftOutput);
        Assert.DoesNotContain("extension EveryProtocol: TestModule.ThemeProtocol", swiftOutput);
    }

    [Fact]
    public void Emit_EmitsProxyForProtocolWithSupportedTypes()
    {
        // Adds protocol to both Types (for C# emission) and Protocols (for Swift EveryProtocol).
        // Verifies that both interface AND proxy class are emitted.
        var (csOutput, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            moduleDecl =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "CounterProtocol",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.CounterProtocol"),
                    MangledName = "$s10TestModule15CounterProtocolP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("count", "Swift.Int", moduleDecl)
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                };
                moduleDecl.Protocols.Add(protocol);
                moduleDecl.Types.Add(protocol);
            });

        // C# interface should be emitted
        Assert.Contains("public interface ICounterProtocol", csOutput);

        // C# proxy class should be emitted
        Assert.Contains("CounterProtocolProxy", csOutput);

        // Swift EveryProtocol conformance should be emitted
        Assert.Contains("extension EveryProtocol: TestModule.CounterProtocol", swiftOutput);
    }

    #endregion

    #region Namespace Emission Tests

    [Fact]
    public void Emit_UsesDefaultNamespacePattern()
    {
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("namespace TestModule", csOutput);
    }

    [Fact]
    public void Emit_UsesCustomNamespacePattern()
    {
        var namespaceResolver = new NamespacePatternResolver("{Module}Bindings");
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>(), namespaceResolver: namespaceResolver);

        Assert.Contains("namespace TestModuleBindings", csOutput);
    }

    [Fact]
    public void Emit_UsesFrameworkPlaceholderInNamespacePattern()
    {
        var namespaceResolver = new NamespacePatternResolver("Acme.{Framework}", frameworkName: "MyKit");
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>(), namespaceResolver: namespaceResolver);

        Assert.Contains("namespace Acme.MyKit", csOutput);
    }

    [Fact]
    public void Emit_ModuleNamedFunctions_WrapperEscalatesToGlobalFunctions()
    {
        // A module literally named "Functions" → namespace Functions.
        // Renaming the wrapper to "Functions" wouldn't help (still stutters).
        // Should escalate to "GlobalFunctions".
        var (csOutput, _) = EmitModuleWithDependencies("Functions", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.Methods.Add(new MethodDecl
                {
                    Name = "doSomething",
                    MangledName = "$s9Functions11doSomethingSiyF",
                    MethodType = MethodType.Static,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new()
                        {
                            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                            Name = string.Empty,
                            PrivateName = string.Empty,
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
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                });
            });

        Assert.Contains("namespace Functions", csOutput);
        Assert.Contains("public partial class GlobalFunctions", csOutput);
        Assert.DoesNotContain("public partial class Functions", csOutput);
    }

    [Fact]
    public void Emit_ModuleWithStutter_WrapperRenamedToFunctions()
    {
        // A module named "Nuke" with namespace Nuke → wrapper should be "Functions"
        var (csOutput, _) = EmitModuleWithDependencies("Nuke", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.Methods.Add(new MethodDecl
                {
                    Name = "loadImage",
                    MangledName = "$s4Nuke9loadImageSiyF",
                    MethodType = MethodType.Static,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new()
                        {
                            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                            Name = string.Empty,
                            PrivateName = string.Empty,
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
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                });
            });

        Assert.Contains("namespace Nuke", csOutput);
        Assert.Contains("public partial class Functions", csOutput);
        Assert.DoesNotContain("public partial class Nuke", csOutput);
    }

    [Fact]
    public void Emit_ModuleWithCustomNamespace_NoStutter_KeepsModuleName()
    {
        // Custom namespace pattern that doesn't stutter → keep module name as wrapper class
        var namespaceResolver = new NamespacePatternResolver("{Module}Bindings");
        var (csOutput, _) = EmitModuleWithDependencies("Nuke", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.Methods.Add(new MethodDecl
                {
                    Name = "loadImage",
                    MangledName = "$s4Nuke9loadImageSiyF",
                    MethodType = MethodType.Static,
                    IsConstructor = false,
                    CSSignature = new List<ArgumentDecl>
                    {
                        new()
                        {
                            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                            Name = string.Empty,
                            PrivateName = string.Empty,
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
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                });
            },
            namespaceResolver: namespaceResolver);

        Assert.Contains("namespace NukeBindings", csOutput);
        Assert.Contains("public partial class Nuke", csOutput);
        Assert.DoesNotContain("public partial class Functions", csOutput);
    }

    #endregion

    #region Helper Methods

    private static (string csOutput, string swiftOutput) EmitModuleWithDependencies(
        string moduleName,
        List<string> dependencies,
        Action<ModuleDecl> customizeModule = null,
        NamespacePatternResolver namespaceResolver = null)
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

        var handler = new ModuleHandler(new Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHandler>(), namespaceResolver);
        var env = handler.Marshal(moduleDecl, typeDatabase);

        // Create a minimal conductor for the test
        var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var conductor = new Conductor(loggerFactory, namespaceResolver);

        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csStringWriter.ToString(), swiftStringWriter.ToString());
    }

    private static PropertyDecl CreateProtocolProperty(string name, string typeName, ModuleDecl moduleDecl)
    {
        return new PropertyDecl
        {
            Name = name,
            SwiftTypeSpec = new NamedTypeSpec(typeName),
            IsStatic = false,
            HasStorage = false,
            Accessors = new List<AccessorDecl>
            {
                new GetAccessorDecl
                {
                    Method = new MethodDecl
                    {
                        Name = $"{name}_Get",
                        MangledName = $"$s{name}g",
                        MethodType = MethodType.Instance,
                        IsConstructor = false,
                        CSSignature = new List<ArgumentDecl>
                        {
                            new()
                            {
                                SwiftTypeSpec = new NamedTypeSpec(typeName),
                                Name = string.Empty,
                                PrivateName = string.Empty,
                                IsInOut = false,
                                IsGeneric = false,
                                ParentDecl = null,
                                ModuleDecl = moduleDecl
                            }
                        },
                        GenericParameters = new List<GenericArgumentDecl>(),
                        ParentDecl = null,
                        ModuleDecl = moduleDecl,
                        Throws = false,
                        IsAsync = false,
                        Visibility = Visibility.Public
                    }
                }
            },
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    #endregion

    #region Framework Resolver Emission Tests

    [Fact]
    public void Emit_ContainsFrameworkResolverClass()
    {
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("internal static class __SwiftFrameworkResolver_TestModule", csOutput);
    }

    [Fact]
    public void Emit_ContainsModuleInitializerAttribute()
    {
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("[ModuleInitializer]", csOutput);
    }

    [Fact]
    public void Emit_ContainsSwiftFrameworkResolverCall()
    {
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("SwiftFrameworkResolver.RegisterForAssembly", csOutput);
    }

    [Fact]
    public void Emit_FrameworkResolverIsInsideNamespace()
    {
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        // The resolver class should appear between the namespace open and close
        var namespaceStart = csOutput.IndexOf("namespace TestModule");
        var resolverStart = csOutput.IndexOf("__SwiftFrameworkResolver_TestModule");
        var lastBrace = csOutput.LastIndexOf("}");

        Assert.True(namespaceStart >= 0, "namespace not found");
        Assert.True(resolverStart > namespaceStart, "resolver should be inside namespace");
        Assert.True(resolverStart < lastBrace, "resolver should be before closing brace");
    }

    [Fact]
    public void Emit_FrameworkResolverCallsSwiftFrameworkResolver()
    {
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("SwiftFrameworkResolver.RegisterForAssembly", csOutput);
        Assert.DoesNotContain("NativeLibrary.SetDllImportResolver", csOutput);
    }

    [Fact]
    public void Emit_FrameworkResolverUsesGlobalQualification()
    {
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("global::Swift.Runtime.SwiftFrameworkResolver", csOutput);
    }

    [Fact]
    public void Emit_FrameworkResolverClassNameIncludesModuleName()
    {
        // Different module names should produce different class names
        var (csOutput1, _) = EmitModuleWithDependencies("Nuke", new List<string>());
        var (csOutput2, _) = EmitModuleWithDependencies("Lottie", new List<string>());

        Assert.Contains("__SwiftFrameworkResolver_Nuke", csOutput1);
        Assert.DoesNotContain("__SwiftFrameworkResolver_Lottie", csOutput1);
        Assert.Contains("__SwiftFrameworkResolver_Lottie", csOutput2);
        Assert.DoesNotContain("__SwiftFrameworkResolver_Nuke", csOutput2);
    }

    [Fact]
    public void Emit_FrameworkResolverUsesTypeofForAssembly()
    {
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("typeof(__SwiftFrameworkResolver_TestModule).Assembly", csOutput);
    }

    [Fact]
    public void Emit_FrameworkResolverSuppressesCA2255()
    {
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("#pragma warning disable CA2255", csOutput);
        Assert.Contains("#pragma warning restore CA2255", csOutput);
    }

    #endregion

    #region Witness Table Registration Emission Tests

    [Fact]
    public void Emit_EmitsRegisterWitnessTableForHashableConformance()
    {
        // When a type has an ISwiftHashable conformance recorded on the emission context,
        // the emitted [ModuleInitializer] should contain a RegisterWitnessTable call.
        var csOutput = EmitModuleWithPrePopulatedConformances(
            "TestModule",
            swiftObjectTypes: new[] { "MyStruct" },
            conformances: new[] { ("MyStruct", "ISwiftHashable") });

        Assert.Contains("RegisterWitnessTable<MyStruct, ISwiftHashable>()", csOutput);
    }

    [Fact]
    public void Emit_DoesNotEmitRegisterWitnessTableForNonHashableConformance()
    {
        // IEquatable and other non-ISwiftHashable conformances should NOT emit RegisterWitnessTable.
        var csOutput = EmitModuleWithPrePopulatedConformances(
            "TestModule",
            swiftObjectTypes: new[] { "MyStruct" },
            conformances: new[] { ("MyStruct", "IEquatable<MyStruct>") });

        Assert.DoesNotContain("RegisterWitnessTable", csOutput);
    }

    [Fact]
    public void Emit_EmitsRegisterWitnessTableAlongsideConformanceFactory()
    {
        // When a type conforms to ISwiftHashable, both RegisterConformanceFactory and
        // RegisterWitnessTable should be emitted.
        var csOutput = EmitModuleWithPrePopulatedConformances(
            "TestModule",
            swiftObjectTypes: new[] { "MyStruct" },
            conformances: new[] { ("MyStruct", "ISwiftHashable") });

        Assert.Contains("RegisterConformanceFactory<MyStruct, ISwiftHashable>()", csOutput);
        Assert.Contains("RegisterWitnessTable<MyStruct, ISwiftHashable>()", csOutput);
    }

    [Fact]
    public void Emit_EmitsRegisterWitnessTableForMultipleHashableTypes()
    {
        // Multiple types conforming to ISwiftHashable should each get a RegisterWitnessTable call.
        var csOutput = EmitModuleWithPrePopulatedConformances(
            "TestModule",
            swiftObjectTypes: new[] { "TypeA", "TypeB" },
            conformances: new[]
            {
                ("TypeA", "ISwiftHashable"),
                ("TypeB", "ISwiftHashable"),
                ("TypeA", "IEquatable<TypeA>")
            });

        Assert.Contains("RegisterWitnessTable<TypeA, ISwiftHashable>()", csOutput);
        Assert.Contains("RegisterWitnessTable<TypeB, ISwiftHashable>()", csOutput);
        // Only 2 RegisterWitnessTable calls (one per Hashable conformance, not per Equatable)
        var count = csOutput.Split("RegisterWitnessTable").Length - 1;
        Assert.Equal(2, count);
    }

    /// <summary>
    /// Helper that emits a module with pre-populated emission context entries (factory types + conformances),
    /// bypassing the full type handler pipeline. This directly tests EmitFrameworkResolver output.
    /// </summary>
    private static string EmitModuleWithPrePopulatedConformances(
        string moduleName,
        string[] swiftObjectTypes,
        (string TypeName, string ProtocolInterface)[] conformances)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            Dependencies = new List<string>(),
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

        // Create a fresh emission context and populate it
        var emissionCtx = new ModuleEmissionContext();
        foreach (var typeName in swiftObjectTypes)
            emissionCtx.RecordSwiftObjectType(typeName);
        foreach (var (typeName, protocolName) in conformances)
            emissionCtx.RecordConformance(typeName, protocolName);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);

        var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        // Use a TypeHandlerContext with our custom emission context
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: emissionCtx);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return csStringWriter.ToString();
    }

    #endregion

    #region IsMangledNameFromModule Tests

    [Theory]
    [InlineData("$s11CryptoSwift3AESCfd", "CryptoSwift", true)]
    [InlineData("$s11CryptoSwift6SHA256V", "CryptoSwift", true)]
    [InlineData("$sSl", "CryptoSwift", false)]
    [InlineData("$ss17FixedWidthIntegerP", "CryptoSwift", false)]
    [InlineData("$sSB", "CryptoSwift", false)]
    [InlineData("$s4Nuke11ImageLoaderCN", "Nuke", true)]
    [InlineData("$s4Nuke11ImageLoaderCN", "CryptoSwift", false)]
    [InlineData("", "CryptoSwift", false)]
    [InlineData("$s11CryptoSwift3AESCfd", "", false)]
    public void IsMangledNameFromModule_CorrectlyIdentifiesModuleOrigin(string mangledName, string moduleName, bool expected)
    {
        Assert.Equal(expected, ModuleHandler.IsMangledNameFromModule(mangledName, moduleName));
    }

    #endregion

    #region CQ-3: Module-Internal Type Suppression Tests

    [Fact]
    public void Emit_InternalProtocol_ExcludedFromEveryProtocol()
    {
        // Issue Q: Internal/@_spi protocols should not be included in EveryProtocol conformance.
        // StripeCore regression: 884 errors from conforming to internal protocols.
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            customizeModule: module =>
            {
                // Public protocol — should be included
                var publicProtocol = new ProtocolDecl
                {
                    Name = "PublicProto",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PublicProto"),
                    MangledName = "$s10TestModule11PublicProtoP",
                    IsModuleInternal = false,
                    HasSelfRequirement = false,
                    IsClassBound = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("value", "Swift.Int", module)
                    },
                    Methods = new List<MethodDecl>(),
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    ParentDecl = module,
                    ModuleDecl = module
                };

                // Internal/@_spi protocol — should be excluded
                var internalProtocol = new ProtocolDecl
                {
                    Name = "InternalProto",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.InternalProto"),
                    MangledName = "$s10TestModule13InternalProtoP",
                    IsModuleInternal = true,
                    HasSelfRequirement = false,
                    IsClassBound = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("secret", "Swift.Int", module)
                    },
                    Methods = new List<MethodDecl>(),
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    ParentDecl = module,
                    ModuleDecl = module
                };

                module.Protocols.Add(publicProtocol);
                module.Protocols.Add(internalProtocol);
            });

        // Public protocol should be conformable
        Assert.Contains("TestModule.PublicProto", swiftOutput);
        // Internal protocol should NOT be in the EveryProtocol conformance
        Assert.DoesNotContain("TestModule.InternalProto", swiftOutput);
    }

    [Fact]
    public void Emit_ProtocolWithInternalTypeInMethodSignature_ExcludedFromEveryProtocol()
    {
        // Issue Q continued: Even if a protocol is public, if its method signatures
        // reference types from the current module that are not in the type database
        // (i.e., internal types), EveryProtocol can't conform because the wrapper
        // module can't access those types.
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            customizeModule: module =>
            {
                // Protocol whose method takes an internal type parameter
                var protocolWithInternalType = new ProtocolDecl
                {
                    Name = "InternalTypedProto",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.InternalTypedProto"),
                    MangledName = "$s10TestModule18InternalTypedProtoP",
                    IsModuleInternal = false, // protocol itself is public
                    HasSelfRequirement = false,
                    IsClassBound = false,
                    Properties = new List<PropertyDecl>(),
                    Methods = new List<MethodDecl>
                    {
                        new()
                        {
                            Name = "process",
                            MangledName = "$s10TestModule18InternalTypedProtoP7processyyAA0E7ContextVF",
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
                                    ModuleDecl = module
                                },
                                new()
                                {
                                    // "TestModule.InternalContext" is NOT in the type database
                                    SwiftTypeSpec = new NamedTypeSpec("TestModule.InternalContext"),
                                    Name = "context",
                                    PrivateName = "context",
                                    IsInOut = false,
                                    IsGeneric = false,
                                    ParentDecl = null,
                                    ModuleDecl = module
                                }
                            },
                            Throws = false,
                            IsAsync = false,
                            GenericParameters = new List<GenericArgumentDecl>(),
                            Visibility = Visibility.Public,
                            ParentDecl = null,
                            ModuleDecl = module
                        }
                    },
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    ParentDecl = module,
                    ModuleDecl = module
                };

                // Protocol with only public types — should be included
                var publicProtocol = new ProtocolDecl
                {
                    Name = "PublicTypedProto",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.PublicTypedProto"),
                    MangledName = "$s10TestModule15PublicTypedProtoP",
                    IsModuleInternal = false,
                    HasSelfRequirement = false,
                    IsClassBound = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("count", "Swift.Int", module)
                    },
                    Methods = new List<MethodDecl>(),
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    ParentDecl = module,
                    ModuleDecl = module
                };

                module.Protocols.Add(protocolWithInternalType);
                module.Protocols.Add(publicProtocol);
            });

        // Protocol with internal type reference should NOT be in EveryProtocol
        Assert.DoesNotContain("TestModule.InternalTypedProto", swiftOutput);
        // Protocol with only public/standard types should be included
        Assert.Contains("TestModule.PublicTypedProto", swiftOutput);
    }

    #endregion
}
