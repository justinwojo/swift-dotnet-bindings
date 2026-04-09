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
        public void Emit_TargetFramework_IsExplicitlyVersioned()
        {
            // The generator-emitted csproj must NOT use a versionless
            // <TargetFramework>net10.0-ios</TargetFramework>: .NET 10 library projects
            // default to the OLDEST installed Apple TPV (apps float, libraries don't),
            // which would silently desync from the version-qualified buildTransitive/
            // pack path on any multi-workload machine. The TFM and pack path must both
            // source from PlatformInfo.PackTfm.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                var defaultPi = PlatformInfoFactory.Create(ApplePlatform.iOS);
                Assert.Contains($"<TargetFramework>{defaultPi.PackTfm}</TargetFramework>", content);
                Assert.DoesNotContain("<TargetFramework>net10.0-ios</TargetFramework>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_TargetFramework_AndBuildTransitive_AreConsistent()
        {
            // Regression gate for the Session 7 trap: today's pack flow only produced an
            // internally-consistent nupkg by coincidence (single Apple workload installed),
            // because the TFM came from pi.Tfm (versionless) and the buildTransitive path
            // came from pi.PackTfm (version-qualified). Both must now source from the same
            // PackTfm value so they cannot drift.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                var defaultPi = PlatformInfoFactory.Create(ApplePlatform.iOS);
                Assert.Contains($"<TargetFramework>{defaultPi.PackTfm}</TargetFramework>", content);
                Assert.Contains($"buildTransitive/{defaultPi.PackTfm}/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_PlatformVersionOverride_FlowsThroughBothFragments()
        {
            // The --platform-version CLI flag (threaded through PlatformInfoFactory.Create)
            // must rewrite BOTH the <TargetFramework> element and the buildTransitive/ pack
            // path to the same overridden value. The Session 7 reproducer at
            // 0.8.0-storekit2-exploration.md uses 26.2 — pin that here.
            var dir = CreateTempDir();
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "StoreKit.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);
                var pi262 = PlatformInfoFactory.Create(ApplePlatform.iOS, "26.2");
                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "StoreKit",
                    Metadata = new XCFrameworkMetadata
                    {
                        LibraryVersion = "26.2.0",
                        PackageVersion = "26.2.0",
                        IsVersionPlaceholder = false,
                        MinimumOSVersion = "16.0",
                        EffectiveMinimumOSVersion = "16.0",
                        SdkVersion = null,
                        ModuleName = "StoreKit",
                        Platforms = new List<string>()
                    },
                    SourceXCFrameworkPath = sourceXcfwPath,
                    SwiftRuntimeVersion = "0.8.0",
                    PlatformInfo = pi262,
                }, _logger);
                var content = File.ReadAllText(Path.Combine(dir, "StoreKit.Swift.iOS.csproj"));
                Assert.Contains("<TargetFramework>net10.0-ios26.2</TargetFramework>", content);
                Assert.Contains("buildTransitive/net10.0-ios26.2/", content);
                // The default 26.0 form must NOT leak through.
                Assert.DoesNotContain("net10.0-ios26.0", content);
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
        public void Emit_IsPackable_True_WhenPublishedRuntimeVersion()
        {
            // Real published runtime versions take the PackageReference path. Pack-time
            // emits a nupkg whose only Swift.Runtime dependency resolves to a real published
            // SwiftBindings.Runtime nupkg, so the project is allowed to be packable.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0", swiftRuntimeVersion: "0.8.0");
                Assert.Contains("<IsPackable>true</IsPackable>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_IsPackable_False_WhenDevSentinelRuntimeVersion()
        {
            // Dev-sentinel projects resolve Swift.Runtime via an in-tree ProjectReference,
            // and `dotnet pack` would (a) emit a phantom `SwiftBindings.Runtime 0.0.0-dev`
            // package dependency and (b) NOT roll the in-tree native dylibs into the outer
            // .nupkg. The result would be an unusable nupkg whose dependency doesn't exist
            // anywhere. Refusing to pack is the loud failure mode — to publish, the caller
            // must pass --swift-runtime-version <published-version> so the PackageReference
            // path is taken and Swift.Runtime's NuGet buildTransitive targets carry the
            // dylib copy logic for consumers.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                Assert.Contains("<IsPackable>false</IsPackable>", content);
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

        [Fact]
        public void Emit_DisablesDefaultCompileItems()
        {
            // The generator already lists every emitted .cs file explicitly via
            // <Compile Include="..." />, so the SDK's default Compile wildcard would
            // double-count them and trip NETSDK1022 ("Duplicate 'Compile' items")
            // when a consumer runs `dotnet build` directly against the emitted csproj.
            // The property must live inside the csproj (NOT on the command line) so
            // it stays scoped to this project — Swift.Runtime relies on default Compile
            // items and would break if the property propagated globally.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", "12.8.0", "15.0");
                Assert.Contains("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitAndRead(string dir, string module, string version, string minOS,
            string? swiftRuntimeVersion = null)
        {
            EmitProject(dir, module, version, minOS, swiftRuntimeVersion: swiftRuntimeVersion);
            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static void EmitProject(string dir, string module, string version, string minOS,
            string? wrapperPath = null, string? swiftRuntimeVersion = null)
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
                WrapperXCFrameworkPath = wrapperPath,
                SwiftRuntimeVersion = swiftRuntimeVersion
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
                Assert.Contains("buildTransitive/net10.0-ios26.0/", content);
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

    /// <summary>
    /// Direct-mode (system-framework) emission. SourceXCFrameworkPath is null because
    /// Apple system frameworks live on-device under /System/Library/Frameworks/ and
    /// resolve at runtime via dyld @rpath; the csproj should omit the source NativeReference
    /// and the source pack item entirely while keeping the wrapper xcframework wiring.
    /// </summary>
    public class BindingProjectDirectModeTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_NoSourceXcframework_OmitsSourceNativeReference()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitDirect(dir, "StoreKit");
                // The wrapper helper, runtime ref, and Compile items must still be present.
                Assert.Contains("<Compile Include=\"StoreKit.cs\" />", content);
                Assert.Contains("SwiftBindings.Runtime", content);
                // The source NativeReference must NOT be emitted.
                Assert.DoesNotContain("Include=\"StoreKit.xcframework\"", content);
                Assert.DoesNotContain("Include=\"../StoreKit.xcframework\"", content);
                Assert.DoesNotContain("runtimes/ios-arm64/native/StoreKit.xcframework/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_NoSourceXcframework_KeepsTargetsPackItem()
        {
            // The {PackageId}.targets pack item is unconditional — consumers still need
            // it for buildTransitive injection even when the source binary is on-device.
            var dir = CreateTempDir();
            try
            {
                var content = EmitDirect(dir, "StoreKit");
                Assert.Contains("StoreKit.Swift.iOS.targets", content);
                Assert.Contains("buildTransitive/net10.0-ios26.0/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_NoSourceXcframework_WithWrapperXcframework_EmitsWrapperRef()
        {
            // The wrapper xcframework is the SBW_ helper dylib — it MUST appear as a
            // NativeReference even when the source xcframework is omitted, otherwise
            // the C# bindings would have no library to bind their helper P/Invokes
            // against at compile/pack time.
            var dir = CreateTempDir();
            try
            {
                var wrapperPath = Path.Combine(dir, "StoreKitSwiftBindings.xcframework");
                Directory.CreateDirectory(wrapperPath);

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "StoreKit",
                    Metadata = CreateMetadata("StoreKit"),
                    SourceXCFrameworkPath = null,
                    WrapperXCFrameworkPath = wrapperPath,
                }, _logger);

                var content = File.ReadAllText(Path.Combine(dir, "StoreKit.Swift.iOS.csproj"));
                Assert.Contains("StoreKitSwiftBindings.xcframework", content);
                Assert.DoesNotContain("Include=\"StoreKit.xcframework\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_NoSourceXcframework_ProducesValidXml()
        {
            // Sanity check: the omission must not leave dangling whitespace or unclosed
            // tags that would break MSBuild evaluation.
            var dir = CreateTempDir();
            try
            {
                var content = EmitDirect(dir, "StoreKit");
                var doc = System.Xml.Linq.XDocument.Parse(content);
                Assert.NotNull(doc.Root);
                Assert.Equal("Project", doc.Root!.Name.LocalName);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitDirect(string dir, string module)
        {
            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = CreateMetadata(module),
                SourceXCFrameworkPath = null,
            }, _logger);
            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static XCFrameworkMetadata CreateMetadata(string module) => new()
        {
            LibraryVersion = null,
            PackageVersion = "0.0.0",
            IsVersionPlaceholder = true,
            MinimumOSVersion = null,
            EffectiveMinimumOSVersion = "16.0",
            SdkVersion = null,
            ModuleName = module,
            Platforms = new List<string>()
        };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_direct_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Local-dev runtime resolution. The default <c>0.0.0-dev</c> sentinel has no published
    /// nupkg, so the emitter must conditionally emit a HintPath <c>Reference</c> against the
    /// in-tree Swift.Runtime build (gated on <c>$(SwiftBindingsRepoRoot)</c>) and pin the
    /// fallback PackageReference to an exact-version range so a stale cached
    /// SwiftBindings.Runtime can't silently satisfy a minimum-version request.
    /// </summary>
    public class BindingProjectRuntimeReferenceTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_DevSentinel_EmitsProjectReferenceGatedOnRepoRootProperty()
        {
            // ProjectReference (NOT a bare <Reference>+<HintPath>) is required so the in-tree
            // Swift.Runtime project's `<Content Include="../native/.../libSwiftBindingsRuntime.dylib">`
            // items copy through to the consumer. A raw assembly reference would compile cleanly
            // but ship a project missing its native concurrency runtime at run/pack time.
            var content = EmitWithRuntimeVersion(BindingProjectEmitter.DefaultSwiftRuntimeVersion);
            Assert.Contains(
                "<ProjectReference Include=\"$(SwiftBindingsRepoRoot)/src/Swift.Runtime/src/Swift.Runtime.csproj\"",
                content);
            Assert.Contains("Condition=\"'$(SwiftBindingsRepoRoot)' != ''\"", content);
            // Reject the previous Reference+HintPath shape — that produced a project
            // that compiled but missed Swift.Runtime's native dylib copy items.
            Assert.DoesNotContain("<HintPath>", content);
            Assert.DoesNotContain("<Reference Include=\"Swift.Runtime\"", content);
        }

        [Fact]
        public void Emit_DevSentinel_PinsFallbackPackageReferenceToExactVersion()
        {
            // Without the bracket pinning, NuGet treats Version="0.0.0-dev" as "minimum
            // 0.0.0-dev" and a stale 0.7.0 cached package will silently satisfy the request,
            // producing 190+ CS errors against types that exist in current Swift.Runtime
            // source. The bracketed exact-version form forces NU1102 instead.
            var content = EmitWithRuntimeVersion(BindingProjectEmitter.DefaultSwiftRuntimeVersion);
            Assert.Contains(
                "<PackageReference Include=\"SwiftBindings.Runtime\" Version=\"[0.0.0-dev]\" Condition=\"'$(SwiftBindingsRepoRoot)' == ''\" />",
                content);
        }

        [Fact]
        public void Emit_PublishedVersion_EmitsBoundedPackageReferenceWithoutProjectReference()
        {
            // Real published versions go through the published-path PackageReference. The
            // version is emitted as a bounded NuGet range (e.g. "[0.8.0,0.9.0)") so future
            // ABI-compatible patch releases of SwiftBindings.Runtime float forward through
            // existing Apple-framework consumers without a republish, but a future minor
            // bump (which is allowed to break ABI) cannot silently resolve into older
            // bindings. The dev-sentinel branches must also NOT appear, otherwise external
            // consumers would see a dangling SwiftBindingsRepoRoot reference and a phantom
            // ProjectReference that can't resolve outside the repo.
            var content = EmitWithRuntimeVersion("0.8.0");
            Assert.Contains(
                "<PackageReference Include=\"SwiftBindings.Runtime\" Version=\"[0.8.0,0.9.0)\" />",
                content);
            // Reject the previous unbounded shape — `Version="0.8.0"` is minimum-only and
            // would let a hypothetical 0.9.0 with a different struct layout silently satisfy
            // the request.
            Assert.DoesNotContain(
                "<PackageReference Include=\"SwiftBindings.Runtime\" Version=\"0.8.0\" />",
                content);
            Assert.DoesNotContain("<HintPath>", content);
            Assert.DoesNotContain("SwiftBindingsRepoRoot", content);
            Assert.DoesNotContain("Swift.Runtime.csproj", content);
        }

        [Theory]
        [InlineData("0.8.0", "[0.8.0,0.9.0)")]
        [InlineData("0.10.0", "[0.10.0,0.11.0)")]
        [InlineData("1.2.3", "[1.2.3,1.3.0)")]
        [InlineData("0.8.0-preview.1", "[0.8.0-preview.1,0.9.0)")]
        public void BuildBoundedRuntimeVersionRange_FloatsPatchSlamsAtNextMinor(string version, string expected)
        {
            // Pin the bounded range shape: lower bound is the exact version (so prerelease
            // suffixes survive), upper bound is the next minor with a `.0` patch and an
            // exclusive `)`. The float-up-to-but-not-including-next-minor shape is what
            // matches the "patch is ABI-compatible, minor is allowed to break" contract.
            Assert.Equal(expected, BindingProjectEmitter.BuildBoundedRuntimeVersionRange(version));
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("1")]
        [InlineData("not.a.semver")]
        [InlineData("x.8.0")]   // non-integer major — would otherwise produce "[x.8.0,x.9.0)"
        [InlineData(".8.0")]    // empty major
        public void BuildBoundedRuntimeVersionRange_FallsBackOnUnparseableInput(string version)
        {
            // Defensive: an upstream tool that hands the emitter a malformed version
            // string should not crash the emit. The fallback is to emit the raw value;
            // NuGet will surface its own restore-time error if the value is unusable.
            // Both major AND minor must parse cleanly as integers — otherwise the
            // computed upper bound would itself be a non-numeric string and NuGet
            // would reject the resulting range with no actionable diagnostic.
            Assert.Equal(version, BindingProjectEmitter.BuildBoundedRuntimeVersionRange(version));
        }

        private static string EmitWithRuntimeVersion(string runtimeVersion)
        {
            var dir = CreateTempDir();
            try
            {
                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "StoreKit",
                    Metadata = new XCFrameworkMetadata
                    {
                        LibraryVersion = null,
                        PackageVersion = "0.0.0",
                        IsVersionPlaceholder = true,
                        MinimumOSVersion = null,
                        EffectiveMinimumOSVersion = "16.0",
                        SdkVersion = null,
                        ModuleName = "StoreKit",
                        Platforms = new List<string>()
                    },
                    SourceXCFrameworkPath = null,
                    SwiftRuntimeVersion = runtimeVersion,
                }, _logger);
                return File.ReadAllText(Path.Combine(dir, "StoreKit.Swift.iOS.csproj"));
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_runtime_{Guid.NewGuid():N}");
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
        public void Emit_WithoutObjCProjectFileName_NoObjCProjectReference()
        {
            // The dev-sentinel Swift.Runtime branch also emits a ProjectReference, so
            // this test must scope to the ObjC mixed-framework comment+block specifically
            // rather than asserting "no ProjectReference anywhere in the file".
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", objcProjectFileName: null);
                Assert.DoesNotContain("ObjC binding project", content);
                Assert.DoesNotContain(".ObjC.iOS.csproj", content);
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

    #region G. Bridge NativeRef Tests

    public class BindingProjectBridgeTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_WithBridge_BridgeNativeRefPresent()
        {
            var dir = CreateTempDir();
            try
            {
                var bridgePath = Path.Combine(dir, "NukeBridge.xcframework");
                Directory.CreateDirectory(bridgePath);

                var content = EmitAndRead(dir, "Nuke", bridgePath: bridgePath);
                Assert.Contains("NukeBridge.xcframework", content);
                Assert.Contains("<Kind>Framework</Kind>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithoutBridge_NoBridgeNativeRef()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", bridgePath: null);
                Assert.DoesNotContain("NukeBridge.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithBridge_BridgePackItemPresent()
        {
            var dir = CreateTempDir();
            try
            {
                var bridgePath = Path.Combine(dir, "NukeBridge.xcframework");
                Directory.CreateDirectory(bridgePath);

                var content = EmitAndRead(dir, "Nuke", bridgePath: bridgePath);
                Assert.Contains("NukeBridge.xcframework/**", content);
                Assert.Contains("runtimes/ios-arm64/native/NukeBridge.xcframework/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_BridgeAndWrapper_BothPresent()
        {
            var dir = CreateTempDir();
            try
            {
                var wrapperPath = Path.Combine(dir, "NukeSwiftBindings.xcframework");
                Directory.CreateDirectory(wrapperPath);
                var bridgePath = Path.Combine(dir, "NukeBridge.xcframework");
                Directory.CreateDirectory(bridgePath);

                var content = EmitAndRead(dir, "Nuke", wrapperPath: wrapperPath, bridgePath: bridgePath);
                Assert.Contains("NukeSwiftBindings.xcframework", content);
                Assert.Contains("NukeBridge.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_HasBridgeSwift_EmitsBridgeRefsWithoutXCFramework()
        {
            // P2 regression test: on first run, bridge .swift exists but xcframework doesn't yet.
            // The generated .csproj should still contain bridge NativeReference/pack items
            // with Exists() conditions so they activate once the bridge is compiled.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Nuke", hasBridgeSwift: true);
                Assert.Contains("NukeBridge.xcframework", content);
                Assert.Contains("Condition=\"Exists('NukeBridge.xcframework')\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitAndRead(string dir, string module,
            string? wrapperPath = null, string? bridgePath = null, bool hasBridgeSwift = false)
        {
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = CreateMinimalMetadata(module),
                SourceXCFrameworkPath = sourceXcfwPath,
                HasBridgeSwift = hasBridgeSwift,
                WrapperXCFrameworkPath = wrapperPath,
                BridgeXCFrameworkPath = bridgePath,
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
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_bridge_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion
}
