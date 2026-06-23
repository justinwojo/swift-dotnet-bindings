// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
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
                    IsSynthesizedAccessor = false,
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
                            IsSynthesizedAccessor = false,
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

    [Fact]
    public void EmitSwiftImports_ImportsSiblingModuleReferencedByType()
    {
        // Regression: FamilyControls protocols reference ManagedSettings.Token without
        // ManagedSettings appearing in the module's declared dependencies list.
        // The scanner must still emit `import ManagedSettings`.
        var (_, swiftOutput) = EmitModuleWithDependencies(
            "FamilyControls",
            new List<string>(),
            moduleDecl =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "TokenHolder",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("FamilyControls.TokenHolder"),
                    MangledName = "$s14FamilyControls11TokenHolderP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("token", "ManagedSettings.Token", moduleDecl)
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                };
                moduleDecl.Protocols.Add(protocol);
            });

        Assert.Contains("import ManagedSettings", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_ImportsMatterWhenMatterSupportSurfaceReferencesIt()
    {
        // Regression for the MatterSupport wrapper-import gap. MatterAddDeviceRequest.setupPayload has type
        // Matter.MTRSetupPayload; the wrapper Swift must emit `import Matter` or swiftc
        // fails with "cannot find type 'Matter' in scope". The gate is now data-driven via
        // apple-frameworks.json's wrapperImportable field — Matter has it set.
        var (_, swiftOutput) = EmitModuleWithDependencies(
            "MatterSupport",
            new List<string>(),
            moduleDecl =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "MatterAddDeviceRequest",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("MatterSupport.MatterAddDeviceRequest"),
                    MangledName = "$s13MatterSupport22MatterAddDeviceRequestP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("setupPayload", "Matter.MTRSetupPayload", moduleDecl)
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                };
                moduleDecl.Protocols.Add(protocol);
            });

        // Whole-line assert: substring "import Matter" also matches the unconditional bound-
        // module line "import MatterSupport", so the original bug would have passed an
        // Assert.Contains check. Split on newlines and require an exact `import Matter` line.
        var importLines = swiftOutput.Split('\n').Select(l => l.TrimEnd('\r'));
        Assert.Contains("import Matter", importLines);
        Assert.Contains("import MatterSupport", importLines);
    }

    [Fact]
    public void EmitSwiftImports_RemapsSpiModuleToPublicCounterpart()
    {
        // Regression: types from SPI (`_`-prefixed) Swift modules registered with a
        // namespaceRemap must import the public counterpart, not the SPI name.
        var (_, swiftOutput) = EmitModuleWithDependencies(
            "TestModule",
            new List<string>(),
            moduleDecl =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "LocationSource",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.LocationSource"),
                    MangledName = "$s10TestModule14LocationSourceP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>(),
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("current", "_LocationEssentials.CLLocation", moduleDecl)
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                };
                moduleDecl.Protocols.Add(protocol);
            });

        Assert.Contains("import CoreLocation", swiftOutput);
        Assert.DoesNotContain("import _LocationEssentials", swiftOutput);
    }

    #endregion

    #region Dependency Module Import Tests

    [Fact]
    public void EmitSwiftImports_ImportsDependencyModuleNames_LegacyFallbackWhenNoSwiftInterface()
    {
        // Backward-compat: when SwiftInterfacePath is null (apple-framework-mode unit tests,
        // direct-mode without `-s/--swiftinterface`, etc.) the emitter falls back to legacy
        // emit-all behavior so every DependencyModuleNames entry still produces an import.
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.DependencyModuleNames = new List<string> { "DocScan" };
            });

        Assert.Contains("import DocScan", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_EmitsDirectlyImportedDependency()
    {
        // When SwiftInterfacePath is set and the bound module's source explicitly
        // `import`s the dep module, the wrapper emits the matching `import` line.
        var interfaceFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(interfaceFile,
                "// swift-interface-format-version: 1.0\n" +
                "import Foundation\n" +
                "import RecaptchaInterop\n" +
                "public struct Marker {}\n");

            var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
                moduleDecl =>
                {
                    moduleDecl.DependencyModuleNames = new List<string> { "RecaptchaInterop" };
                    moduleDecl.SwiftInterfacePath = interfaceFile;
                });

            Assert.Contains("import RecaptchaInterop", swiftOutput);
        }
        finally
        {
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public void EmitSwiftImports_FiltersUnreferencedDependency()
    {
        // Regression: the validation pipeline auto-broadcasts every sibling xcframework
        // as `--framework-dependency`, including C++-only siblings (absl/grpc/leveldb/
        // openssl_grpc/grpcpp). The bound module's swiftinterface doesn't import them,
        // and their Clang umbrella headers can't compile in swiftc without `-Xcc
        // -std=c++17` flags. EmitSwiftImports must drop unreferenced deps when a
        // swiftinterface is available.
        var interfaceFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(interfaceFile,
                "// swift-interface-format-version: 1.0\n" +
                "import Foundation\n" +
                "public struct Marker {}\n");

            var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
                moduleDecl =>
                {
                    moduleDecl.DependencyModuleNames = new List<string> { "absl" };
                    moduleDecl.SwiftInterfacePath = interfaceFile;
                });

            Assert.DoesNotContain("import absl", swiftOutput);
        }
        finally
        {
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public void EmitSwiftImports_EmitsDependencyReferencedAsQualifier()
    {
        // Regression: a module references a sibling's type inline in its swiftinterface
        // (cross-module protocol cast) but has no explicit `import SiblingModule` line.
        // The filter must keep the dep when its module name appears as a type qualifier —
        // otherwise the wrapper compile fails with "cannot find type 'X' in scope".
        var interfaceFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(interfaceFile,
                "// swift-interface-format-version: 1.0\n" +
                "import Foundation\n" +
                "public class Foo {\n" +
                "  public func bar() -> (any CloudPlatformSdkRemoteConfigInterop.RemoteConfigInterop)? { nil }\n" +
                "}\n");

            var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
                moduleDecl =>
                {
                    moduleDecl.DependencyModuleNames = new List<string> { "CloudPlatformSdkRemoteConfigInterop" };
                    moduleDecl.SwiftInterfacePath = interfaceFile;
                });

            Assert.Contains("import CloudPlatformSdkRemoteConfigInterop", swiftOutput);
        }
        finally
        {
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public void EmitSwiftImports_QualifierMatchIsWholeWord()
    {
        // `FooBar` in the swiftinterface must NOT count as a reference to module `Foo`.
        // Word-boundary matching is what keeps "absl" from being preserved when a
        // bound module mentions some unrelated `abslBenchmark` symbol.
        var interfaceFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(interfaceFile,
                "// swift-interface-format-version: 1.0\n" +
                "import Foundation\n" +
                "public class FooBar { public init() {} }\n");

            var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
                moduleDecl =>
                {
                    moduleDecl.DependencyModuleNames = new List<string> { "Foo" };
                    moduleDecl.SwiftInterfacePath = interfaceFile;
                });

            // `Foo` is in DependencyModuleNames but only appears as a prefix of `FooBar`
            // in the interface. Whole-word match must reject it.
            Assert.DoesNotContain("import Foo\n", swiftOutput);
        }
        finally
        {
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public void EmitSwiftImports_EmitsDeclaredImportEvenWhenDependencyModuleNamesEmpty()
    {
        // Regression: some SDKs ship as static archives, so otool-based
        // auto-detection finds nothing and DependencyModuleNames stays empty during
        // generation. The bound module's swiftinterface still declares `import X` for
        // every sibling its public API needs. The wrapper must emit those declared
        // imports directly — without them, the wrapper compile fails with
        // "cannot find type 'X' in scope" when the sibling is broadcast via
        // --framework-dependency at compile time.
        var interfaceFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(interfaceFile,
                "// swift-interface-format-version: 1.0\n" +
                "import CloudPlatformSdkRemoteConfigInterop\n" +
                "import Foundation\n" +
                "import Swift\n" +
                "import _Concurrency\n" +
                "import _StringProcessing\n" +
                "public struct Marker {}\n");

            var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
                moduleDecl =>
                {
                    moduleDecl.DependencyModuleNames = new List<string>();
                    moduleDecl.SwiftInterfacePath = interfaceFile;
                });

            Assert.Contains("import CloudPlatformSdkRemoteConfigInterop", swiftOutput);
            // Stdlib internals must be dropped.
            Assert.DoesNotContain("import _Concurrency", swiftOutput);
            Assert.DoesNotContain("import _StringProcessing", swiftOutput);
        }
        finally
        {
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public void EmitSwiftImports_SkipsAppleFrameworkDeclaredImports()
    {
        // Regression: when a module's swiftinterface declares `import Network` but the
        // module has a type that collides with `Network.Framer`, emitting `import Network`
        // unconditionally causes swiftc to fail with "ambiguous type lookup". Apple
        // frameworks are imported on demand by the scanned-imports mechanism only when
        // a wrapper signature actually references one of their types. Declared-but-
        // unreferenced Apple imports must be skipped.
        var interfaceFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(interfaceFile,
                "// swift-interface-format-version: 1.0\n" +
                "import Network\n" +
                "import Foundation\n" +
                "public class Framer { public init() {} }\n");

            var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
                moduleDecl =>
                {
                    moduleDecl.DependencyModuleNames = new List<string>();
                    moduleDecl.SwiftInterfacePath = interfaceFile;
                });

            // Network is a known Apple framework — must NOT be emitted just because it
            // appears in the bound module's swiftinterface.
            Assert.DoesNotContain("import Network", swiftOutput);
        }
        finally
        {
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public void EmitSwiftImports_SkipsImplementationOnlyDeclaredImport()
    {
        // Regression: `@_implementationOnly import absl` in the bound module's
        // swiftinterface must NOT carry into the wrapper. absl is a C++-only sibling
        // and swiftc cannot load it without -Xcc -std=c++17. Non-public attribute
        // imports are not part of the wrapper's compile surface.
        var interfaceFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(interfaceFile,
                "// swift-interface-format-version: 1.0\n" +
                "@_implementationOnly import absl\n" +
                "import Foundation\n" +
                "public struct Marker {}\n");

            var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
                moduleDecl =>
                {
                    moduleDecl.DependencyModuleNames = new List<string>();
                    moduleDecl.SwiftInterfacePath = interfaceFile;
                });

            Assert.DoesNotContain("import absl", swiftOutput);
        }
        finally
        {
            File.Delete(interfaceFile);
        }
    }

    [Fact]
    public void EmitSwiftImports_SkipsNonPublicImportEvenWhenInDependencyModuleNames()
    {
        // Regression: a sibling listed in DependencyModuleNames (because the
        // upstream resolver auto-broadcasts it as `--framework-dependency`) MUST
        // still be dropped when the bound module marks it `@_implementationOnly`
        // / `private` / `internal` / `fileprivate` / `package`. Without the
        // non-public filter, the DependencyModuleNames branch short-circuits
        // the public-import checks and emits `import absl` anyway.
        var interfaceFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(interfaceFile,
                "// swift-interface-format-version: 1.0\n" +
                "private import absl\n" +
                "import Foundation\n" +
                "public struct Marker {}\n");

            var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
                moduleDecl =>
                {
                    moduleDecl.DependencyModuleNames = new List<string> { "absl" };
                    moduleDecl.SwiftInterfacePath = interfaceFile;
                });

            Assert.DoesNotContain("import absl", swiftOutput);
        }
        finally
        {
            File.Delete(interfaceFile);
        }
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

    [Fact]
    public void EmitSwiftImports_RemapsDependencyModuleThroughCompileImport()
    {
        // Regression: dependency modules (--framework-dependency) marked @_implementationOnly
        // by their umbrella must be remapped at the literal import line, just like the primary
        // module is. Otherwise a sibling module that depends on RealityFoundation would emit
        // `import RealityFoundation` and hit the same @_implementationOnly compiler error that affects primaries.
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.DependencyModuleNames = new List<string> { "RealityFoundation" };
            });

        Assert.Contains("import RealityKit", swiftOutput);
        Assert.DoesNotContain("import RealityFoundation", swiftOutput);
    }

    [Fact]
    public void EmitSwiftImports_RemapDedupesAgainstPrimaryUmbrella()
    {
        // RealityKit binding itself: the primary module already emits `import RealityKit`.
        // A dependency on RealityFoundation must remap to RealityKit and dedupe — the line
        // should appear exactly once.
        var (_, swiftOutput) = EmitModuleWithDependencies("RealityKit", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.DependencyModuleNames = new List<string> { "RealityFoundation" };
            });

        Assert.Equal(1, EmitterTestHelpers.CountOccurrences(swiftOutput, "import RealityKit"));
        Assert.DoesNotContain("import RealityFoundation", swiftOutput);
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

    [Fact]
    public void Emit_EntityRootedProtocol_RoutesThroughEveryEntityProtocolViaModulePipeline()
    {
        // Failure B end-to-end through the real ModuleHandler.Emit pipeline:
        // the class-superclass filter at the LINQ-pipeline gate must let
        // an Entity-rooted protocol through, the EmitEveryProtocolClass
        // pre-scan must register the Entity-base flag, EveryEntityProtocol
        // (+ four @_cdecl wrappers) must be emitted, and the per-protocol
        // routing must hang the extension off EveryEntityProtocol rather
        // than skip it via HasClassSuperclassRequirement. The direct-emitter
        // unit tests in EveryProtocolEmitterTests cover the pre-scan in
        // isolation; this test exercises the integration so the filter
        // exception, pre-scan list, and conformance routing stay in sync.
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            customizeModule: module =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "HasAnchoring",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.HasAnchoring"),
                    MangledName = "$s10TestModule12HasAnchoringP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>
                    {
                        new("RealityFoundation.Entity"),
                    },
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("anchorIdentifier", "Swift.Int", module),
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = module,
                    ModuleDecl = module,
                };
                module.Protocols.Add(protocol);
                module.Types.Add(protocol);
            },
            registerExtraTypes: typeDatabase =>
            {
                var realityFoundation = new ModuleTypeDatabase(
                    "RealityFoundation",
                    "/fake/RealityFoundation.framework/RealityFoundation");
                var entityName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.Entity");
                realityFoundation.RegisterType(entityName, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", "Entity"),
                    SwiftTypeName = entityName,
                    MetadataAccessor = "$s17RealityFoundation6EntityCMa",
                    Flags = TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class,
                });
                typeDatabase.AddModuleDatabase(realityFoundation);
            });

        // EveryEntityProtocol class definition must be emitted, gated on
        // AnyEntityBaseUsed in EmitEveryProtocolClass — without the filter
        // exception, the protocol never reaches suitableProtocols, the
        // pre-scan finds nothing, and AnyEntityBaseUsed stays false.
        Assert.Contains("public final class EveryEntityProtocol", swiftOutput);
        Assert.Contains("@_cdecl(\"SBW_CreateEveryEntityProtocol\")", swiftOutput);
        Assert.Contains("@_cdecl(\"SBW_ReleaseEveryEntityProtocol\")", swiftOutput);
        Assert.Contains("@_cdecl(\"SBW_GetMetadata_EveryEntityProtocol\")", swiftOutput);
        Assert.Contains("@_cdecl(\"SBW_SetEveryEntityProtocolDeinitCallback\")", swiftOutput);

        // The per-protocol conformance extension must hang off
        // EveryEntityProtocol — not EveryProtocol or EveryObjCProtocol.
        Assert.Contains("extension EveryEntityProtocol: TestModule.HasAnchoring", swiftOutput);
        Assert.DoesNotContain("extension EveryProtocol: TestModule.HasAnchoring", swiftOutput);
        Assert.DoesNotContain("extension EveryObjCProtocol: TestModule.HasAnchoring", swiftOutput);
    }

    [Fact]
    public void Emit_NonEntityClassSuperclassProtocol_StillSkippedByModulePipeline()
    {
        // Negative counterpart to the Entity test above. A protocol whose
        // class-superclass requirement is anything other than Entity
        // (eg UIGestureRecognizer) must still be filtered out by the
        // suitableProtocols pipeline — the exception added to the filter
        // is Entity-specific. Without this assertion, a regression that
        // widens IsEntityRootedProtocol would silently produce extensions
        // EveryEntityProtocol cannot satisfy at the Swift type-check.
        var (_, swiftOutput) = EmitModuleWithDependencies("TestModule", new List<string>(),
            customizeModule: module =>
            {
                var protocol = new ProtocolDecl
                {
                    Name = "GestureProto",
                    SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.GestureProto"),
                    MangledName = "$s10TestModule12GestureProtoP",
                    Types = new List<TypeDecl>(),
                    Operators = new List<OperatorDecl>(),
                    GenericSignature = null,
                    AssociatedTypes = new List<AssociatedTypeDecl>(),
                    InheritedProtocols = new List<NamedTypeSpec>
                    {
                        new("UIKit.UIGestureRecognizer"),
                    },
                    IsClassBound = false,
                    HasSelfRequirement = false,
                    Properties = new List<PropertyDecl>
                    {
                        CreateProtocolProperty("gestureId", "Swift.Int", module),
                    },
                    Methods = new List<MethodDecl>(),
                    Subscripts = new List<SubscriptDecl>(),
                    ParentDecl = module,
                    ModuleDecl = module,
                };
                module.Protocols.Add(protocol);
                module.Types.Add(protocol);
            },
            registerExtraTypes: typeDatabase =>
            {
                var uikit = new ModuleTypeDatabase("UIKit", "/fake/UIKit.framework/UIKit");
                var gestureName = SwiftTypeName.FromModuleQualifiedName("UIKit.UIGestureRecognizer");
                uikit.RegisterType(gestureName, new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName("UIKit", "UIGestureRecognizer"),
                    SwiftTypeName = gestureName,
                    MetadataAccessor = "$sSo19UIGestureRecognizerCMa",
                    Flags = TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement,
                    Kind = TypeRecordKind.Class,
                });
                typeDatabase.AddModuleDatabase(uikit);
            });

        Assert.DoesNotContain("public final class EveryEntityProtocol", swiftOutput);
        Assert.DoesNotContain("extension EveryEntityProtocol: TestModule.GestureProto", swiftOutput);
        Assert.DoesNotContain("extension EveryProtocol: TestModule.GestureProto", swiftOutput);
        Assert.DoesNotContain("extension EveryObjCProtocol: TestModule.GestureProto", swiftOutput);
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

    #region Payload Construction Semantics Registration (Finding 11)

    [Fact]
    public void Emit_RegistersRecordedPayloadSemantics_InModuleInitializer()
    {
        // Finding 11: each emitted ISwiftObject type's declared PayloadConstructionSemantics is
        // recorded on the emission context during type emission, and ModuleHandler turns each
        // recorded entry into a SwiftMarshal.RegisterPayloadSemantics(typeof(T), ...) call in the
        // module initializer — so the unconstrained marshal seam resolves the by-Type contract from
        // a seeded cache instead of the reflection backstop at runtime. Both the typeof argument and
        // the literal enum value must round-trip through the register loop.
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>(),
            preEmitHook: ctx =>
            {
                ctx.RecordPayloadSemantics("TestModule.Widget", Swift.Runtime.PayloadConstructionSemantics.Adopt);
                ctx.RecordPayloadSemantics("TestModule.Buffer", Swift.Runtime.PayloadConstructionSemantics.Copy);
            });

        Assert.Contains(
            "RegisterPayloadSemantics(typeof(TestModule.Widget), global::Swift.Runtime.PayloadConstructionSemantics.Adopt)",
            csOutput);
        Assert.Contains(
            "RegisterPayloadSemantics(typeof(TestModule.Buffer), global::Swift.Runtime.PayloadConstructionSemantics.Copy)",
            csOutput);
    }

    [Fact]
    public void Emit_AssertsRuntimeContractVersion_TiedToPackageMinor()
    {
        // The emitted handshake epoch is DERIVED from the single-sourced package version
        // (major*1000 + minor), not a hand-maintained literal — so the binding's load-time epoch,
        // the runtime's RuntimeContract.Version, and the bounded NuGet range cannot silently drift
        // apart. This guard ties all three to one parse of one source. (Before deriving, the literal
        // was 2 while epoch("0.0.0-dev") is 0 — i.e. this assertion was RED with the old hand const.)
        var expectedEpoch = RuntimeVersionRange.Epoch(BindingProjectEmitter.DefaultSwiftRuntimeVersion);

        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains($"global::Swift.Runtime.RuntimeContract.AssertCompatible({expectedEpoch})", csOutput);
        Assert.Equal(expectedEpoch, ModuleHandler.EmittedRuntimeContractVersion);

        // Cross-side lockstep: the generator's emitted epoch and the runtime's own derived epoch are
        // both single-sourced from the same package version, so they must agree at every build (both
        // 0 in dev, both the minor at a release).
        Assert.Equal(Swift.Runtime.RuntimeContract.Version, ModuleHandler.EmittedRuntimeContractVersion);

        // The two epoch parsers (generator-side RuntimeVersionRange.Epoch, runtime-side
        // RuntimeContract.ParseEpoch — necessarily duplicated since the runtime can't reference
        // generator code) must map identically, or the lockstep above could pass in dev yet diverge
        // at a release version.
        foreach (var v in new[] { "0.0.0-dev", "0.15.3", "0.16.0", "0.16.0-preview.1", "1.0.0", "1.15.0", "x.8.0" })
            Assert.Equal(RuntimeVersionRange.Epoch(v), Swift.Runtime.RuntimeContract.ParseEpoch(v));
    }

    [Fact]
    public void Emit_RuntimeContractEpoch_FollowsTargetedRuntime_NotBakedDefault()
    {
        // When a binding pins an OLDER runtime via --swift-runtime-version, its bounded NuGet
        // range follows that pin — but the load-time handshake epoch must follow it TOO, or restore
        // succeeds against the pinned runtime while [ModuleInitializer] hard-aborts at load (the
        // asserted epoch sitting above the older runtime's supported window). Program.cs derives the
        // pin's epoch into ModuleEmissionContext.RuntimeContractEpoch; emission must honor it over the
        // baked default. Here we inject a distinct epoch to stand in for the pin.
        var defaultEpoch = RuntimeVersionRange.Epoch(BindingProjectEmitter.DefaultSwiftRuntimeVersion);
        var pinnedEpoch = RuntimeVersionRange.Epoch("0.16.0"); // 16 — chosen distinct from the dev default (0).
        Assert.NotEqual(defaultEpoch, pinnedEpoch);

        var (csOutput, _) = EmitModuleWithDependencies(
            "TestModule",
            new List<string>(),
            preEmitHook: ctx => ctx.RuntimeContractEpoch = pinnedEpoch);

        Assert.Contains($"global::Swift.Runtime.RuntimeContract.AssertCompatible({pinnedEpoch})", csOutput);
        // The baked default is fully overridden — the pinned epoch is the only one emitted.
        Assert.DoesNotContain($"AssertCompatible({defaultEpoch})", csOutput);
    }

    #endregion

    #region Conformance Registration Concrete-Literal Guard

    [Fact]
    public void Emit_OpenGenericConformanceType_FailsClosedRatherThanEmittingMonoUnsafeRegistration()
    {
        // Mono-safety invariant: the module initializer's RegisterConformanceFactory<TType, …> and
        // RegisterWitnessTable<TType, …> both invoke a static-virtual on TType, which crashes the
        // Mono JIT when TType is an OPEN generic. The conformance recorder skips open-generic types,
        // so an open-generic TType reaching the emit loop is a recorder regression. Emission must
        // fail closed at generation time rather than ship a binding that crashes the consumer.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EmitModuleWithDependencies(
                "TestModule",
                new List<string>(),
                preEmitHook: ctx => ctx.RecordConformance("Box<T>", "IFoo")));

        Assert.Contains("Box<T>", ex.Message);
        Assert.Contains("concrete-literal", ex.Message);
    }

    [Fact]
    public void Emit_ConcreteTypeConformingToClosedGenericInterface_EmitsRegistrationWithoutThrowing()
    {
        // The guard is on the TType (left) operand ONLY. A concrete type conforming to a CLOSED
        // generic protocol interface (e.g. Codec.Encoding : IEquatable<Codec.Encoding>) is fully
        // Mono-safe — the static-virtual dispatches on the concrete Codec.Encoding — and must not be
        // rejected just because the protocol operand contains a '<'.
        var (csOutput, _) = EmitModuleWithDependencies(
            "TestModule",
            new List<string>(),
            preEmitHook: ctx => ctx.RecordConformance(
                "Codec.Encoding",
                "global::System.IEquatable<Codec.Encoding>"));

        Assert.Contains(
            "RegisterConformanceFactory<Codec.Encoding, global::System.IEquatable<Codec.Encoding>>()",
            csOutput);
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
    public void Emit_RemappedNamespace_ErrorRegistryHelperEmittedUnderResolvedNamespace()
    {
        // Pipeline-level lock for the StoreKit2 regression: ModuleHandler.Emit must
        // populate ModuleEmissionContext.ResolvedNamespace BEFORE invoking
        // ErrorRegistryHelperEmitter.EmitCSharpRegistryIfNeeded. A future refactor
        // that reorders those steps would leave ResolvedNamespace null at the read
        // site, and the helper would fall back to the raw Swift module name —
        // emitting a global::StoreKit._SbwModuleErrorRegistry_StoreKit path that
        // does not resolve to any C# namespace in the consumer csproj.
        //
        // The Swift module name "StoreKit" mirrors the production trigger (StoreKit2
        // csproj sets NamespacePattern="StoreKit2" for Swift module "StoreKit").
        var namespaceResolver = new NamespacePatternResolver("StoreKit2");
        var (csOutput, _) = EmitModuleWithDependencies(
            "StoreKit",
            new List<string>(),
            namespaceResolver: namespaceResolver,
            preEmitHook: ctx => ctx.RegisterErrorTypeId("StoreKit.SKError"));

        // Helper class is emitted inside the resolved C# namespace.
        Assert.Contains("namespace StoreKit2", csOutput);
        Assert.Contains("_SbwModuleErrorRegistry_StoreKit", csOutput);

        // The registered error type is rebased to the resolved namespace.
        Assert.Contains("global::StoreKit2.SKError", csOutput);

        // No stale Swift-module-qualified path leaks into the dispatch body — those
        // would fail to compile in the consumer csproj because there's no C#
        // namespace named "StoreKit" under that binding project.
        Assert.DoesNotContain("global::StoreKit.SKError", csOutput);
        Assert.DoesNotContain("global::StoreKit._SbwModuleErrorRegistry_StoreKit", csOutput);
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
                    IsSynthesizedAccessor = false,
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
        // A module whose name matches its top-level namespace → wrapper should be renamed to "Functions"
        var (csOutput, _) = EmitModuleWithDependencies("ImagePipeline", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.Methods.Add(new MethodDecl
                {
                    Name = "loadImage",
                    MangledName = "$s13ImagePipeline9loadImageSiyF",
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
                    IsSynthesizedAccessor = false,
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                });
            });

        Assert.Contains("namespace ImagePipeline", csOutput);
        Assert.Contains("public partial class Functions", csOutput);
        Assert.DoesNotContain("public partial class ImagePipeline", csOutput);
    }

    [Fact]
    public void Emit_ModuleWithCustomNamespace_NoStutter_KeepsModuleName()
    {
        // Custom namespace pattern that doesn't stutter → keep module name as wrapper class
        var namespaceResolver = new NamespacePatternResolver("{Module}Bindings");
        var (csOutput, _) = EmitModuleWithDependencies("ImagePipeline", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.Methods.Add(new MethodDecl
                {
                    Name = "loadImage",
                    MangledName = "$s13ImagePipeline9loadImageSiyF",
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
                    IsSynthesizedAccessor = false,
                    ParentDecl = moduleDecl,
                    ModuleDecl = moduleDecl
                });
            },
            namespaceResolver: namespaceResolver);

        Assert.Contains("namespace ImagePipelineBindings", csOutput);
        Assert.Contains("public partial class ImagePipeline", csOutput);
        Assert.DoesNotContain("public partial class Functions", csOutput);
    }

    [Fact]
    public void Emit_LabelOnlyDistinctFreeFunctions_KeepBothViaSecondaryDedup()
    {
        // Two free functions with same Swift name + same parameter type but different
        // argument labels (e.g., describe(forItem:) vs describe(fromValue:)) must both be
        // emitted: primary dedup uses the labelled signature, secondary dedup detects
        // the projected C# collision and disambiguates the second with a numeric suffix.
        // Wires the actual ModuleHandler.Emit pipeline (not just the dedup helpers).
        var moduleDecl = new ModuleDecl
        {
            Name = "LabelDedup",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
        moduleDecl.Methods.Add(BuildLabelledMethod("describe", "forItem",
            "$s10LabelDedup8describe7forItemySi_tF", moduleDecl));
        moduleDecl.Methods.Add(BuildLabelledMethod("describe", "fromValue",
            "$s10LabelDedup8describe9fromValueySi_tF", moduleDecl));

        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);
        typeDatabase.AddModuleDatabase(new ModuleTypeDatabase("LabelDedup", "/fake/path"));

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);
        var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        var csOutput = csStringWriter.ToString();

        // Both methods kept — neither bumped to "DuplicateSignature" skip.
        Assert.DoesNotContain("DuplicateSignature", csOutput);

        // Projected name appears twice: once unsuffixed, once suffixed via secondary dedup.
        var bareCount = System.Text.RegularExpressions.Regex.Matches(csOutput, @"\bDescribe\s*\(").Count;
        var suffixedCount = System.Text.RegularExpressions.Regex.Matches(csOutput, @"\bDescribe2\s*\(").Count;
        Assert.True(bareCount >= 1, $"Expected at least one 'Describe(' call site in output:\n{csOutput}");
        Assert.True(suffixedCount >= 1, $"Expected at least one 'Describe2(' call site in output:\n{csOutput}");
    }

    private static MethodDecl BuildLabelledMethod(string name, string argLabel, string mangledName, ModuleDecl moduleDecl)
    {
        return new MethodDecl
        {
            Name = name,
            MangledName = mangledName,
            MethodType = MethodType.Static,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                // Return type (void)
                new()
                {
                    SwiftTypeSpec = TupleTypeSpec.Empty,
                    Name = string.Empty,
                    PrivateName = string.Empty,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                },
                // Single Int parameter with the differentiating argument label.
                new()
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = argLabel,
                    PrivateName = argLabel,
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            },
            Throws = false,
            IsAsync = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            IsSynthesizedAccessor = false,
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
    }

    #endregion

    #region Helper Methods

    private static (string csOutput, string swiftOutput) EmitModuleWithDependencies(
        string moduleName,
        List<string> dependencies,
        Action<ModuleDecl> customizeModule = null,
        NamespacePatternResolver namespaceResolver = null,
        Action<ModuleEmissionContext> preEmitHook = null,
        Action<TypeDatabase> registerExtraTypes = null)
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
        registerExtraTypes?.Invoke(typeDatabase);

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHandler>(), namespaceResolver);
        var env = handler.Marshal(moduleDecl, typeDatabase);

        // Create a minimal conductor for the test
        var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var conductor = new Conductor(loggerFactory, namespaceResolver);

        // Pre-emit hook receives a fresh per-test emission context so tests that
        // mutate state (e.g. RegisterErrorTypeId) don't leak into the shared
        // ModuleEmissionContext.Default singleton used by other tests.
        TypeHandlerContext context;
        if (preEmitHook != null)
        {
            var emissionContext = new ModuleEmissionContext();
            preEmitHook(emissionContext);
            context = TypeHandlerContext.Empty with { EmissionContext = emissionContext };
        }
        else
        {
            context = TypeHandlerContext.Empty;
        }

        handler.Emit(csWriter, swiftWriter, env, conductor, context);

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
                        IsSynthesizedAccessor = false
                    }
                }
            },
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    [Fact]
    public void Emit_DoesNotEmitDependencyNamespaceUsingDirectives()
    {
        // Dependency using directives are not emitted — the generator uses fully-qualified
        // names for cross-module type references. Adding bare using directives would cause
        // compilation failures when dependency assemblies aren't referenced.
        var (csOutput, _) = EmitModuleWithDependencies("ImagePipelineUI", new List<string>(),
            moduleDecl =>
            {
                moduleDecl.DependencyModuleNames = new List<string> { "ImagePipeline" };
            });

        Assert.DoesNotContain("using ImagePipeline;", csOutput);
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
        var (csOutput1, _) = EmitModuleWithDependencies("ImagePipeline", new List<string>());
        var (csOutput2, _) = EmitModuleWithDependencies("VectorAnimation", new List<string>());

        Assert.Contains("__SwiftFrameworkResolver_ImagePipeline", csOutput1);
        Assert.DoesNotContain("__SwiftFrameworkResolver_VectorAnimation", csOutput1);
        Assert.Contains("__SwiftFrameworkResolver_VectorAnimation", csOutput2);
        Assert.DoesNotContain("__SwiftFrameworkResolver_ImagePipeline", csOutput2);
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

    [Fact]
    public void Emit_ModuleInitializer_AssertsRuntimeContractVersion()
    {
        // Finding 32: the module initializer must perform the runtime-contract handshake
        // so a binding generated against an incompatible runtime fails loudly at load.
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        Assert.Contains("global::Swift.Runtime.RuntimeContract.AssertCompatible(", csOutput);
    }

    [Fact]
    public void Emit_ModuleInitializer_ContractCheckPrecedesRegistrations()
    {
        // The contract check is the single unconditional guard BEFORE the best-effort
        // (try/catch) framework-resolver and factory registrations — so an incompatible
        // binding cannot silently fall through to a later dispatch failure.
        var (csOutput, _) = EmitModuleWithDependencies("TestModule", new List<string>());

        var assertIdx = csOutput.IndexOf("RuntimeContract.AssertCompatible(");
        var resolverIdx = csOutput.IndexOf("SwiftFrameworkResolver.RegisterForAssembly");

        Assert.True(assertIdx >= 0, "AssertCompatible call not found");
        Assert.True(resolverIdx >= 0, "RegisterForAssembly call not found");
        Assert.True(assertIdx < resolverIdx,
            "AssertCompatible must precede the framework-resolver registration");
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
    public void Emit_EmitsRegisterWitnessTableForAllConformances()
    {
        // All protocol conformances should emit RegisterWitnessTable (not just ISwiftHashable).
        // Pre-registering witness tables during module init avoids runtime SIGKILL on NativeAOT device.
        var csOutput = EmitModuleWithPrePopulatedConformances(
            "TestModule",
            swiftObjectTypes: new[] { "MyStruct" },
            conformances: new[] { ("MyStruct", "IEquatable<MyStruct>") });

        Assert.Contains("RegisterWitnessTable<MyStruct, IEquatable<MyStruct>>()", csOutput);
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
    public void Emit_EmitsRegisterWitnessTableForAllConformanceTypes()
    {
        // All conformances should get RegisterWitnessTable calls (not just ISwiftHashable).
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
        Assert.Contains("RegisterWitnessTable<TypeA, IEquatable<TypeA>>()", csOutput);
        // 3 RegisterWitnessTable calls (one per conformance)
        var count = csOutput.Split("RegisterWitnessTable").Length - 1;
        Assert.Equal(3, count);
    }

    [Fact]
    public void Emit_BoundGenericType_RegisteredInModuleInitializer()
    {
        // Fix F: Closed generic types (Pair<CoordinateRef, LabelRef>) must be pre-registered
        // in the module initializer for NativeAOT. Without this, NativeAOT trims the explicit
        // ISwiftObject.GetTypeMetadata() on closed generics, causing MarshalFromSwift<T> to fail.
        var emissionCtx = new ModuleEmissionContext();
        emissionCtx.RecordSwiftObjectType("CoordinateRef");
        emissionCtx.RecordBoundGenericSwiftObjectType("TestModule.Pair<TestModule.CoordinateRef, TestModule.LabelRef>");

        var csOutput = EmitModuleWithEmissionContext("TestModule", emissionCtx);

        // Both non-generic and closed generic types should appear in module initializer
        Assert.Contains("RegisterSwiftObjectFactory<CoordinateRef>()", csOutput);
        Assert.Contains("RegisterSwiftObjectFactory<TestModule.Pair<TestModule.CoordinateRef, TestModule.LabelRef>>()", csOutput);
        Assert.Contains("GetTypeMetadata", csOutput);
    }

    [Fact]
    public void RecordBoundGenericSwiftObjectType_SkipsNonGenericTypes()
    {
        var emissionCtx = new ModuleEmissionContext();
        emissionCtx.RecordBoundGenericSwiftObjectType("SimpleType");

        // Non-generic types should not be recorded via this method
        Assert.Empty(emissionCtx.EmittedSwiftObjectTypes);
    }

    [Fact]
    public void RecordBoundGenericSwiftObjectType_DeduplicatesEntries()
    {
        var emissionCtx = new ModuleEmissionContext();
        emissionCtx.RecordBoundGenericSwiftObjectType("Mod.Pair<Mod.A, Mod.B>");
        emissionCtx.RecordBoundGenericSwiftObjectType("Mod.Pair<Mod.A, Mod.B>");

        Assert.Single(emissionCtx.EmittedSwiftObjectTypes);
    }

    [Fact]
    public void RecordBoundGenericSwiftObjectType_SkipsOpenGenerics()
    {
        var emissionCtx = new ModuleEmissionContext();
        emissionCtx.RecordBoundGenericSwiftObjectType("Box<T>");
        emissionCtx.RecordBoundGenericSwiftObjectType("Pair<T1, T2>");

        // Open generics (unresolved type params) should not be recorded
        Assert.Empty(emissionCtx.EmittedSwiftObjectTypes);
    }

    [Fact]
    public void RecordBoundGenericSwiftObjectType_SkipsNestedOpenGenerics()
    {
        // P2: Nested generics like Outer<Mod.Pair<T, Mod.B>, Mod.C> must NOT be recorded.
        // The naive Split(',') approach misclassifies this as closed because fragments
        // contain dots from the outer type. The depth-aware parser catches it.
        var emissionCtx = new ModuleEmissionContext();
        emissionCtx.RecordBoundGenericSwiftObjectType("Outer<Mod.Pair<T, Mod.B>, Mod.C>");

        Assert.Empty(emissionCtx.EmittedSwiftObjectTypes);
    }

    [Fact]
    public void RecordBoundGenericSwiftObjectType_AcceptsNestedClosedGenerics()
    {
        var emissionCtx = new ModuleEmissionContext();
        emissionCtx.RecordBoundGenericSwiftObjectType("Outer<Mod.Pair<Mod.A, Mod.B>, Mod.C>");

        // All top-level args are qualified: "Mod.Pair<Mod.A, Mod.B>" and "Mod.C"
        Assert.Single(emissionCtx.EmittedSwiftObjectTypes);
    }

    [Theory]
    [InlineData("A<B.X<C.Y, D.Z>, E.W>", new[] { "B.X<C.Y, D.Z>", "E.W" })]
    [InlineData("Pair<Mod.A, Mod.B>", new[] { "Mod.A", "Mod.B" })]
    [InlineData("Box<T>", new[] { "T" })]
    [InlineData("Outer<Mod.Pair<T, Mod.B>, Mod.C>", new[] { "Mod.Pair<T, Mod.B>", "Mod.C" })]
    public void SplitTopLevelTypeArgs_ParsesCorrectly(string typeName, string[] expected)
    {
        var result = ModuleEmissionContext.SplitTopLevelTypeArgs(typeName);
        Assert.Equal(expected, result);
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

    /// <summary>
    /// Helper that emits a module with a pre-populated ModuleEmissionContext.
    /// Used when tests need to call methods like RecordBoundGenericSwiftObjectType directly.
    /// </summary>
    private static string EmitModuleWithEmissionContext(string moduleName, ModuleEmissionContext emissionCtx)
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

        var csStringWriter = new StringWriter();
        var swiftStringWriter = new StringWriter();
        var csWriter = new CSharpWriter(csStringWriter);
        var swiftWriter = new SwiftWriter(swiftStringWriter);

        var handler = new ModuleHandler(new Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHandler>());
        var env = handler.Marshal(moduleDecl, typeDatabase);

        var loggerFactory = new Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory();
        var conductor = new Conductor(loggerFactory);

        var context = new TypeHandlerContext(null, new(), null, EmissionContext: emissionCtx);
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return csStringWriter.ToString();
    }

    #endregion

    #region IsMangledNameFromModule Tests

    [Theory]
    [InlineData("$s9CryptoLib3AESCfd", "CryptoLib", true)]
    [InlineData("$s9CryptoLib6SHA256V", "CryptoLib", true)]
    [InlineData("$sSl", "CryptoLib", false)]
    [InlineData("$ss17FixedWidthIntegerP", "CryptoLib", false)]
    [InlineData("$sSB", "CryptoLib", false)]
    [InlineData("$s13ImagePipeline11ImageLoaderCN", "ImagePipeline", true)]
    [InlineData("$s13ImagePipeline11ImageLoaderCN", "CryptoLib", false)]
    [InlineData("", "CryptoLib", false)]
    [InlineData("$s9CryptoLib3AESCfd", "", false)]
    public void IsMangledNameFromModule_CorrectlyIdentifiesModuleOrigin(string mangledName, string moduleName, bool expected)
    {
        Assert.Equal(expected, ModuleHandler.IsMangledNameFromModule(mangledName, moduleName));
    }

    [Theory]
    // Apple `@_implementationOnly` umbrella collapse: the source module's protocols may carry
    // mangled names that encode the umbrella module. The filter must accept either prefix when
    // apple-frameworks.json registers a compileImportModule remap, otherwise the protocols are
    // silently dropped from the source module's emission pass and downstream proxy references
    // dangle (CS0246 in cross-module consumers).
    [InlineData("$s10RealityKit9ComponentP", "RealityFoundation", true)]
    [InlineData("$s10RealityKit12HasAnchoringP", "RealityFoundation", true)]
    [InlineData("$s10RealityKit21SynchronizationPeerIDP", "RealityFoundation", true)]
    [InlineData("$s10RealityKit22SynchronizationServiceP", "RealityFoundation", true)]
    // Native (non-umbrella) prefix still accepted for the same module.
    [InlineData("$s17RealityFoundation12BindableDataP", "RealityFoundation", true)]
    // Umbrella's own emission pass: the umbrella module name has no remap, so only its native
    // prefix is accepted (no recursive expansion).
    [InlineData("$s10RealityKit9ComponentP", "RealityKit", true)]
    [InlineData("$s17RealityFoundation12BindableDataP", "RealityKit", false)]
    // Other modules are unaffected by RealityFoundation's umbrella mapping.
    [InlineData("$s10RealityKit9ComponentP", "CryptoLib", false)]
    public void IsMangledNameFromModule_AcceptsCompileImportUmbrellaPrefix(string mangledName, string moduleName, bool expected)
    {
        Assert.Equal(expected, ModuleHandler.IsMangledNameFromModule(mangledName, moduleName));
    }

    #endregion

    #region CQ-3: Module-Internal Type Suppression Tests

    [Fact]
    public void Emit_InternalProtocol_ExcludedFromEveryProtocol()
    {
        // Issue Q: Internal/@_spi protocols should not be included in EveryProtocol conformance.
        // Regression: 884 errors from conforming to internal protocols.
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
                            IsSynthesizedAccessor = false,
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

    #region Namespace/Type Collision Tests

    [Fact]
    public void QualifyNamespaceReferences_RenamedNestedType_NotQualified()
    {
        // Bug: When a nested type is renamed (e.g., Connection → ConnectionType to avoid
        // property collision), QualifyNamespaceReferences must use the C# name (ConnectionType),
        // not the Swift name (Connection), in the nestedTypeNames set. Otherwise, qualified
        // references using the renamed type get incorrectly global:: qualified.
        var input = "public static void GetDescription(this NetworkMonitor.ConnectionType self)";
        var nestedTypeNames = new HashSet<string> { "ConnectionType" }; // C# name after rename

        var result = StringEmitter.QualifyNamespaceReferences(input, "NetworkMonitor", nestedTypeNames);

        // Nested type reference should NOT get global:: qualification
        Assert.Contains("NetworkMonitor.ConnectionType", result);
        Assert.DoesNotContain("global::NetworkMonitor.ConnectionType", result);
    }

    [Fact]
    public void QualifyNamespaceReferences_NonNestedType_GetsGlobalQualified()
    {
        // Non-nested types in the same namespace should get global:: qualification
        var input = "public class NetworkMonitorConnectionTypeExtensions : NetworkMonitor.SomeOtherType";
        var nestedTypeNames = new HashSet<string> { "ConnectionType" };

        var result = StringEmitter.QualifyNamespaceReferences(input, "NetworkMonitor", nestedTypeNames);

        // Non-nested type should get global:: qualification
        Assert.Contains("global::NetworkMonitor.SomeOtherType", result);
    }

    [Fact]
    public void QualifyNamespaceReferences_SwiftNameNotInSet_GetsIncorrectlyQualified()
    {
        // Demonstrates the bug: if the SET uses Swift name "Connection" instead of C# name
        // "ConnectionType", the renamed type gets incorrectly qualified.
        var input = "public static void GetDescription(this NetworkMonitor.ConnectionType self)";
        var swiftNames = new HashSet<string> { "Connection" }; // Wrong: Swift name, not C#

        var result = StringEmitter.QualifyNamespaceReferences(input, "NetworkMonitor", swiftNames);

        // With the wrong set, it would get incorrectly qualified
        Assert.Contains("global::NetworkMonitor.ConnectionType", result);
    }

    [Fact]
    public void QualifyNamespaceReferences_TypeDatabaseRename_NestedTypeExcludedViaLookup()
    {
        // Integration test: exercises the same TypeDatabase-driven code path as ModuleEmitter.cs:95-107.
        // If the PrecomputeNestedTypeRenames or TypeDatabase lookup in ModuleEmitter regresses,
        // this test catches it.

        // 1. Set up TypeDatabase with a module where a class name matches the module name
        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("NetworkMonitor", "/tmp/NetworkMonitor.dylib");

        var parentSwiftName = SwiftTypeName.FromModuleQualifiedName("NetworkMonitor.NetworkMonitor");
        module.RegisterType(parentSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("NetworkMonitor", "NetworkMonitor"),
            SwiftTypeName = parentSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.None,
            Kind = TypeRecordKind.Class
        });

        var nestedSwiftName = SwiftTypeName.FromModuleQualifiedName("NetworkMonitor.NetworkMonitor.Connection");
        module.RegisterType(nestedSwiftName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("NetworkMonitor", "NetworkMonitor.Connection"),
            SwiftTypeName = nestedSwiftName,
            MetadataAccessor = "$sMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Enum
        });

        typeDatabase.AddModuleDatabase(module);

        // 2. Create ModuleDecl with a class sharing the module name, containing a nested enum
        //    and a property that collides with the nested type name
        var moduleDecl = new ModuleDecl
        {
            Name = "NetworkMonitor",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var nestedEnumDecl = new EnumDecl
        {
            Name = "Connection",
            SwiftTypeName = nestedSwiftName,
            MangledName = "$sMa",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            IsFrozen = true,
            ParentDecl = null!, // Set below
            ModuleDecl = moduleDecl,
            MetadataAccessor = "$sMa",
        };

        var classDecl = new ClassDecl
        {
            Name = "NetworkMonitor",
            SwiftTypeName = parentSwiftName,
            MangledName = "$sMa",
            Properties = new List<PropertyDecl>
            {
                new()
                {
                    Name = "connection",
                    SwiftTypeSpec = new NamedTypeSpec("NetworkMonitor.NetworkMonitor") { InnerType = new NamedTypeSpec("Connection") },
                    IsStatic = false,
                    HasStorage = true,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null!, // Set implicitly
                    ModuleDecl = moduleDecl
                }
            },
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl> { nestedEnumDecl },
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        nestedEnumDecl.ParentDecl = classDecl;
        moduleDecl.Types.Add(classDecl);

        // 3. Run PrecomputeNestedTypeRenames — this renames Connection → ConnectionType in TypeDatabase
        NameProvider.PrecomputeNestedTypeRenames(moduleDecl, typeDatabase);

        // 4. Build nestedTypeNames the same way ModuleEmitter.cs:92-107 does
        var collisionType = moduleDecl.Types.FirstOrDefault(t => t.Name == "NetworkMonitor");
        Assert.NotNull(collisionType);

        var nestedTypeNames = new HashSet<string>();
        if (collisionType is TypeDecl td)
        {
            foreach (var nested in td.Types)
            {
                var csLeafName = NameProvider.ToPascalCaseForTypeName(nested.Name);
                if (typeDatabase.TryGetTypeRecord(nested.SwiftTypeName, out var nestedRecord))
                {
                    var csName = nestedRecord.CSharpTypeName.Name;
                    var lastDot = csName.LastIndexOf('.');
                    if (lastDot >= 0)
                        csLeafName = csName.Substring(lastDot + 1);
                }
                nestedTypeNames.Add(csLeafName);
            }
        }

        // Verify the set contains the RENAMED C# name, not the Swift name
        Assert.Contains("ConnectionType", nestedTypeNames);
        Assert.DoesNotContain("Connection", nestedTypeNames);

        // 5. QualifyNamespaceReferences with the TypeDatabase-derived set
        var input = "public static void GetDescription(this NetworkMonitor.ConnectionType self)";
        var result = StringEmitter.QualifyNamespaceReferences(input, "NetworkMonitor", nestedTypeNames);

        // Renamed nested type should NOT get global:: qualification
        Assert.Contains("NetworkMonitor.ConnectionType", result);
        Assert.DoesNotContain("global::NetworkMonitor.ConnectionType", result);
    }

    #endregion
}
