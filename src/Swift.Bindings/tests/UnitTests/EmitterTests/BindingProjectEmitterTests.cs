// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. Basic Project Emission Tests

    public class BindingProjectBasicTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_CreatesProjectFile()
        {
            var dir = CreateTempDir();
            try
            {
                EmitProject(dir, "Nuke", "12.8.0", "15.0");
                Assert.True(File.Exists(Path.Combine(dir, "Nuke.Swift.iOS.csproj")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_CorrectPackageId()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                Assert.Contains("<PackageId>Nuke.Swift.iOS</PackageId>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_TargetFramework_IsNet10iOS()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                Assert.Contains("<TargetFramework>net10.0-ios</TargetFramework>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_AllowUnsafeBlocks_Enabled()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                Assert.Contains("<AllowUnsafeBlocks>true</AllowUnsafeBlocks>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_IsPackable_True()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                Assert.Contains("<IsPackable>true</IsPackable>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_PackageVersion_MatchesMetadata()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                Assert.Contains("<PackageVersion>12.8.0</PackageVersion>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SupportedOSPlatformVersion_MatchesMetadata()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "BlinkID", "6.11.0", "16.0");
                Assert.Contains("<SupportedOSPlatformVersion>16.0</SupportedOSPlatformVersion>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftRuntimeReference_Present()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                Assert.Contains("SwiftBindings.Runtime", content);
                Assert.Contains(BindingProjectEmitter.DefaultSwiftRuntimeVersion, content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitAndRead(string dir, string module, string version, string minOS)
        {
            EmitProject(dir, module, version, minOS);
            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static void EmitProject(string dir, string module, string version, string minOS,
            string? wrapperPath = null)
        {
            // Create a fake source xcframework path
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = new XCFrameworkMetadata
                {
                    LibraryVersion = version,
                    PackageVersion = version,
                    IsVersionPlaceholder = false,
                    MinimumOSVersion = minOS,
                    EffectiveMinimumOSVersion = minOS,
                    SdkVersion = null,
                    ModuleName = module,
                    Platforms = new List<string>()
                },
                SourceXCFrameworkPath = sourceXcfwPath,
                WrapperXCFrameworkPath = wrapperPath
            }, _logger);
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region B. Compile Item Tests

    public class BindingProjectCompileItemTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_MainBindingsFile_Included()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke");
                Assert.Contains("<Compile Include=\"Nuke.cs\" />", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WrappersFile_ConditionallyIncluded()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke");
                Assert.Contains("Nuke.Wrappers.cs", content);
                Assert.Contains("Condition=\"Exists('Nuke.Wrappers.cs')\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftUIBridgeFile_ConditionallyIncluded()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke");
                Assert.Contains("Nuke.SwiftUIBridge.cs", content);
                Assert.Contains("Condition=\"Exists('Nuke.SwiftUIBridge.cs')\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ResolvedNamespace_UsedForCompileItems()
        {
            // When ResolvedNamespace differs from ModuleName (e.g., {Framework} pattern),
            // <Compile Include> items must use the resolved namespace, not the module name.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", resolvedNamespace: "NukeUI");
                Assert.Contains("<Compile Include=\"NukeUI.cs\" />", content);
                Assert.Contains("NukeUI.Wrappers.cs", content);
                Assert.Contains("NukeUI.SwiftUIBridge.cs", content);
                // Must NOT contain the module name in compile items
                Assert.DoesNotContain("\"Nuke.cs\"", content);
                Assert.DoesNotContain("Nuke.Wrappers.cs", content);
                Assert.DoesNotContain("Nuke.SwiftUIBridge.cs", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_NoResolvedNamespace_FallsBackToModuleName()
        {
            // When ResolvedNamespace is null (manual mode), file names use ModuleName.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", resolvedNamespace: null);
                Assert.Contains("<Compile Include=\"Nuke.cs\" />", content);
                Assert.Contains("Nuke.Wrappers.cs", content);
                Assert.Contains("Nuke.SwiftUIBridge.cs", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitAndRead(string dir, string module, string? resolvedNamespace = null)
        {
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = CreateMinimalMetadata(module),
                SourceXCFrameworkPath = sourceXcfwPath,
                ResolvedNamespace = resolvedNamespace,
            }, _logger);
            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static XCFrameworkMetadata CreateMinimalMetadata(string module) => new()
        {
            LibraryVersion = "1.0.0",
            PackageVersion = "1.0.0",
            IsVersionPlaceholder = false,
            MinimumOSVersion = "15.0",
            EffectiveMinimumOSVersion = "15.0",
            SdkVersion = null,
            ModuleName = module,
            Platforms = new List<string>()
        };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_ci_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region C. NativeReference and NuGet Layout Tests

    public class BindingProjectNativeRefTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_SourceXCFramework_NativeRefPresent()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", hasWrapper: false);
                Assert.Contains("<NativeReference Include=", content);
                Assert.Contains("Nuke.xcframework", content);
                Assert.Contains("<Kind>Framework</Kind>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithWrapper_WrapperNativeRefPresent()
        {
            var dir = CreateTempDir();
            try
            {
                // Create wrapper xcframework directory
                var wrapperPath = Path.Combine(dir, "NukeSwiftBindings.xcframework");
                Directory.CreateDirectory(wrapperPath);

                var content = EmitAndRead(dir, "Nuke", hasWrapper: true, wrapperPath: wrapperPath);
                Assert.Contains("NukeSwiftBindings.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithoutWrapper_NoWrapperNativeRef()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", hasWrapper: false);
                Assert.DoesNotContain("NukeSwiftBindings.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_TargetsPackItem_Present()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", hasWrapper: false);
                Assert.Contains("Nuke.Swift.iOS.targets", content);
                Assert.Contains("buildTransitive/net10.0-ios/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_XCFrameworkPackItems_Present()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", hasWrapper: false);
                Assert.Contains("runtimes/ios-arm64/native/Nuke.xcframework/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_VersionPlaceholder_EmitsWarningComment()
        {
            var dir = CreateTempDir();
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "Test.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "Test",
                    Metadata = new XCFrameworkMetadata
                    {
                        LibraryVersion = "1.0",
                        PackageVersion = "0.0.0",
                        IsVersionPlaceholder = true,
                        MinimumOSVersion = "15.0",
                        EffectiveMinimumOSVersion = "15.0",
                        SdkVersion = null,
                        ModuleName = "Test",
                        Platforms = new List<string>()
                    },
                    SourceXCFrameworkPath = sourceXcfwPath,
                }, _logger);

                var content = File.ReadAllText(Path.Combine(dir, "Test.Swift.iOS.csproj"));
                Assert.Contains("WARNING", content);
                Assert.Contains("placeholder", content);
                Assert.Contains("<PackageVersion>0.0.0</PackageVersion>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_OverwritesExisting()
        {
            var dir = CreateTempDir();
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "Nuke.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);
                var csprojPath = Path.Combine(dir, "Nuke.Swift.iOS.csproj");
                File.WriteAllText(csprojPath, "old content");

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "Nuke",
                    Metadata = CreateMinimalMetadata("Nuke"),
                    SourceXCFrameworkPath = sourceXcfwPath,
                }, _logger);

                var content = File.ReadAllText(csprojPath);
                Assert.DoesNotContain("old content", content);
                Assert.Contains("<Project Sdk=", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_CustomSwiftRuntimeVersion_Used()
        {
            var dir = CreateTempDir();
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "Nuke.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "Nuke",
                    Metadata = CreateMinimalMetadata("Nuke"),
                    SourceXCFrameworkPath = sourceXcfwPath,
                    SwiftRuntimeVersion = "0.2.0-preview.1"
                }, _logger);

                var content = File.ReadAllText(Path.Combine(dir, "Nuke.Swift.iOS.csproj"));
                Assert.Contains("0.2.0-preview.1", content);
                Assert.DoesNotContain(BindingProjectEmitter.DefaultSwiftRuntimeVersion, content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitAndRead(string dir, string module, bool hasWrapper, string? wrapperPath = null)
        {
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = CreateMinimalMetadata(module),
                SourceXCFrameworkPath = sourceXcfwPath,
                WrapperXCFrameworkPath = hasWrapper ? wrapperPath : null
            }, _logger);
            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static XCFrameworkMetadata CreateMinimalMetadata(string module) => new()
        {
            LibraryVersion = "1.0.0",
            PackageVersion = "1.0.0",
            IsVersionPlaceholder = false,
            MinimumOSVersion = "15.0",
            EffectiveMinimumOSVersion = "15.0",
            SdkVersion = null,
            ModuleName = module,
            Platforms = new List<string>()
        };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_nr_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region D. Framework Dependency Tests

    public class BindingProjectDependencyTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_NoDependencies_NoExtraPackageReference()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", dependencies: null);
                // Should have only the Swift.Runtime PackageReference
                Assert.Contains("SwiftBindings.Runtime", content);
                Assert.DoesNotContain("SmartCardIO", content);
                Assert.DoesNotContain("StripeCore", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_OneDependency_PackageReferenceEmitted()
        {
            var dir = CreateTempDir();
            try
            {
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/path/to/SmartCardIO.xcframework",
                        ModuleName = "SmartCardIO",
                        PackageVersion = "1.2.0"
                    }
                };
                var content = EmitAndRead(dir, "ACSSmartCardIO", dependencies: deps);
                Assert.Contains("<PackageReference Include=\"SmartCardIO.Swift.iOS\" Version=\"1.2.0\" />", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_TwoDependencies_BothPackageReferencesEmitted()
        {
            var dir = CreateTempDir();
            try
            {
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/path/to/StripeCore.xcframework",
                        ModuleName = "StripeCore",
                        PackageVersion = "24.0.0"
                    },
                    new()
                    {
                        XCFrameworkPath = "/path/to/StripeUICore.xcframework",
                        ModuleName = "StripeUICore",
                        PackageVersion = "24.0.0"
                    }
                };
                var content = EmitAndRead(dir, "StripePaymentSheet", dependencies: deps);
                Assert.Contains("StripeCore.Swift.iOS", content);
                Assert.Contains("StripeUICore.Swift.iOS", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_DependencyWithCustomPackageId_UsesCustomId()
        {
            var dir = CreateTempDir();
            try
            {
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/path/to/DepLib.xcframework",
                        ModuleName = "DepLib",
                        PackageVersion = "1.0.0",
                        PackageId = "Custom.DepLib.Package"
                    }
                };
                var content = EmitAndRead(dir, "MainLib", dependencies: deps);
                Assert.Contains("Custom.DepLib.Package", content);
                // Convention ID should NOT appear when custom PackageId is provided
                Assert.DoesNotContain("DepLib.Swift.iOS", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_DependencyWithPlaceholderVersion_WarningComment()
        {
            var dir = CreateTempDir();
            try
            {
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/path/to/Dep.xcframework",
                        ModuleName = "Dep",
                        PackageVersion = null  // Will use 0.0.0 placeholder
                    }
                };
                var content = EmitAndRead(dir, "Main", dependencies: deps);
                Assert.Contains("Version=\"0.0.0\"", content);
                Assert.Contains("Placeholder version", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_DependencyDoesNotAffectSwiftRuntimeRef()
        {
            var dir = CreateTempDir();
            try
            {
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/path/to/Dep.xcframework",
                        ModuleName = "Dep",
                        PackageVersion = "1.0.0"
                    }
                };
                var content = EmitAndRead(dir, "Main", dependencies: deps);
                Assert.Contains("SwiftBindings.Runtime", content);
                Assert.Contains(BindingProjectEmitter.DefaultSwiftRuntimeVersion, content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ObjCOnlyDependency_NoPackageReference()
        {
            var dir = CreateTempDir();
            try
            {
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/path/to/Stripe3DS2.xcframework",
                        ModuleName = "Stripe3DS2",
                        IsObjCOnly = true
                    }
                };
                var content = EmitAndRead(dir, "StripePayments", dependencies: deps);
                // Should have Swift.Runtime but NOT Stripe3DS2
                Assert.Contains("SwiftBindings.Runtime", content);
                Assert.DoesNotContain("Stripe3DS2", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MixedDependencies_OnlySwiftDepsGetPackageReference()
        {
            var dir = CreateTempDir();
            try
            {
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/path/to/StripeCore.xcframework",
                        ModuleName = "StripeCore",
                        PackageVersion = "24.0.0",
                        IsObjCOnly = false
                    },
                    new()
                    {
                        XCFrameworkPath = "/path/to/Stripe3DS2.xcframework",
                        ModuleName = "Stripe3DS2",
                        IsObjCOnly = true
                    }
                };
                var content = EmitAndRead(dir, "StripePayments", dependencies: deps);
                Assert.Contains("StripeCore.Swift.iOS", content);
                Assert.DoesNotContain("Stripe3DS2", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitAndRead(string dir, string module,
            IReadOnlyList<FrameworkDependencyInfo>? dependencies)
        {
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = CreateMinimalMetadata(module),
                SourceXCFrameworkPath = sourceXcfwPath,
                Dependencies = dependencies
            }, _logger);
            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static XCFrameworkMetadata CreateMinimalMetadata(string module) => new()
        {
            LibraryVersion = "1.0.0",
            PackageVersion = "1.0.0",
            IsVersionPlaceholder = false,
            MinimumOSVersion = "15.0",
            EffectiveMinimumOSVersion = "15.0",
            SdkVersion = null,
            ModuleName = module,
            Platforms = new List<string>()
        };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_dep_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region E. ObjC ProjectReference Tests (Mixed Framework)

    public class BindingProjectObjCRefTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_WithObjCProjectFileName_ProjectReferencePresent()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "BlinkID", objcProjectFileName: "BlinkID.ObjC.iOS.csproj");
                Assert.Contains("<ProjectReference Include=\"BlinkID.ObjC.iOS.csproj\" />", content);
                Assert.Contains("mixed framework", content.ToLower());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithoutObjCProjectFileName_NoProjectReference()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", objcProjectFileName: null);
                Assert.DoesNotContain("ProjectReference", content);
                Assert.DoesNotContain("ObjC binding project", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_ObjCProjectReference_HasExistsCondition()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "BlinkID", objcProjectFileName: "BlinkID.ObjC.iOS.csproj");
                Assert.Contains("Condition=\"Exists('BlinkID.ObjC.iOS.csproj')\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitAndRead(string dir, string module, string? objcProjectFileName)
        {
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = CreateMinimalMetadata(module),
                SourceXCFrameworkPath = sourceXcfwPath,
                ObjCProjectFileName = objcProjectFileName
            }, _logger);
            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static XCFrameworkMetadata CreateMinimalMetadata(string module) => new()
        {
            LibraryVersion = "1.0.0",
            PackageVersion = "1.0.0",
            IsVersionPlaceholder = false,
            MinimumOSVersion = "15.0",
            EffectiveMinimumOSVersion = "15.0",
            SdkVersion = null,
            ModuleName = module,
            Platforms = new List<string>()
        };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_objc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion
}
