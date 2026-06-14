// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Runtime.InteropServices;
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
                EmitProject(dir, "ImagePipeline", "12.8.0", "15.0");
                Assert.True(File.Exists(Path.Combine(dir, "ImagePipeline.Swift.iOS.csproj")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_CorrectPackageId()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "12.8.0", "15.0");
                Assert.Contains("<PackageId>ImagePipeline.Swift.iOS</PackageId>", content);
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
                var content = EmitAndRead(dir, "ImagePipeline", "12.8.0", "15.0");
                var defaultPi = PlatformInfoFactory.Create(ApplePlatform.iOS);
                Assert.Contains($"<TargetFramework>{defaultPi.PackTfm}</TargetFramework>", content);
                Assert.DoesNotContain("<TargetFramework>net10.0-ios</TargetFramework>", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_TargetFramework_AndBuildTransitive_AreConsistent()
        {
            // Regression gate: a previous pack flow only produced an internally-consistent
            // nupkg by coincidence (single Apple workload installed), because the TFM came
            // from pi.Tfm (versionless) and the buildTransitive path came from pi.PackTfm
            // (version-qualified). Both must now source from the same PackTfm value so
            // they cannot drift.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", "12.8.0", "15.0");
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
            // path to the same overridden value. 26.2 is the canonical StoreKit2-era
            // repro value — pin that here.
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
                var content = EmitAndRead(dir, "ImagePipeline", "12.8.0", "15.0");
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
                var content = EmitAndRead(dir, "ImagePipeline", "12.8.0", "15.0", swiftRuntimeVersion: "0.8.0");
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
                var content = EmitAndRead(dir, "ImagePipeline", "12.8.0", "15.0");
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
                var content = EmitAndRead(dir, "ImagePipeline", "12.8.0", "15.0");
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
                var content = EmitAndRead(dir, "DocScan", "6.11.0", "16.0");
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
                var content = EmitAndRead(dir, "ImagePipeline", "12.8.0", "15.0");
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
                var content = EmitAndRead(dir, "ImagePipeline", "12.8.0", "15.0");
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
                var content = EmitAndRead(dir, "ImagePipeline");
                Assert.Contains("<Compile Include=\"ImagePipeline.cs\" />", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WrappersFile_ConditionallyIncluded()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline");
                Assert.Contains("ImagePipeline.Wrappers.cs", content);
                Assert.Contains("Condition=\"Exists('ImagePipeline.Wrappers.cs')\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftUIBridgeFile_ConditionallyIncluded()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline");
                Assert.Contains("ImagePipeline.SwiftUIBridge.cs", content);
                // Must include Exists() check AND a TFM gate that excludes native macOS
                // (defense-in-depth alongside the in-source `#if __IOS__ || __TVOS__ ||
                // __MACCATALYST__` — the bridge session classes call into Swift @_cdecl
                // symbols that only exist on UIKit-family platforms).
                Assert.Contains("Exists('ImagePipeline.SwiftUIBridge.cs')", content);
                Assert.Contains("!$(TargetFramework.Contains('-macos'))", content);
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
                var content = EmitAndRead(dir, "ImagePipeline", resolvedNamespace: "ImagePipelineUI");
                Assert.Contains("<Compile Include=\"ImagePipelineUI.cs\" />", content);
                Assert.Contains("ImagePipelineUI.Wrappers.cs", content);
                Assert.Contains("ImagePipelineUI.SwiftUIBridge.cs", content);
                // Must NOT contain the module name in compile items
                Assert.DoesNotContain("\"ImagePipeline.cs\"", content);
                Assert.DoesNotContain("ImagePipeline.Wrappers.cs", content);
                Assert.DoesNotContain("ImagePipeline.SwiftUIBridge.cs", content);
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
                var content = EmitAndRead(dir, "ImagePipeline", resolvedNamespace: null);
                Assert.Contains("<Compile Include=\"ImagePipeline.cs\" />", content);
                Assert.Contains("ImagePipeline.Wrappers.cs", content);
                Assert.Contains("ImagePipeline.SwiftUIBridge.cs", content);
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

    #region B2. Trimmer Descriptor Wiring Tests

    /// <summary>
    /// The generator emits <c>ILLink.Descriptors.xml</c> alongside the binding sources when
    /// the module declares any open-generic ISwiftObject types (RC-AOT). The csproj must
    /// root that file via both <c>EmbeddedResource</c> (so trimmer-mode consumers picking
    /// up the nupkg auto-discover it from the shipped assembly) and <c>TrimmerRootDescriptor</c>
    /// (so the local NativeAOT publish actually picks it up — ILC does NOT auto-discover
    /// descriptors embedded in referenced assemblies). Both items are gated on
    /// <c>Exists()</c> so the csproj stays clean when the generator produced no descriptor.
    /// </summary>
    public class BindingProjectTrimmerDescriptorTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_WritesEmbeddedResourceItemForDescriptor()
        {
            var content = EmitAndRead();
            Assert.Contains("<EmbeddedResource Include=\"ILLink.Descriptors.xml\">", content);
            Assert.Contains("<LogicalName>ILLink.Descriptors.xml</LogicalName>", content);
        }

        [Fact]
        public void Emit_WritesTrimmerRootDescriptorItemForDescriptor()
        {
            var content = EmitAndRead();
            Assert.Contains("<TrimmerRootDescriptor Include=\"ILLink.Descriptors.xml\" />", content);
        }

        [Fact]
        public void Emit_DescriptorItemGroup_IsExistsGated()
        {
            // The csproj is written even when the generator produced no descriptor (module
            // had no open generics). Without the Exists() gate, MSBuild would warn about
            // a missing EmbeddedResource and the trim analyzer would log "descriptor not
            // found" for every consumer. Gating keeps the csproj happy in either case.
            var content = EmitAndRead();
            Assert.Contains(
                "<ItemGroup Condition=\"Exists('ILLink.Descriptors.xml')\">",
                content);
        }

        [Fact]
        public void Emit_PacksDescriptorLooseIntoBuildTransitive()
        {
            // Defect J (binding leg): the EmbeddedResource copy is invisible to ILC, which
            // does NOT auto-discover descriptors embedded in referenced assemblies. So the
            // descriptor must ALSO ship loose in buildTransitive/<tfm>/ — adjacent to the
            // consumer .targets that root it via $(MSBuildThisFileDirectory) for a downstream
            // PackageReference consumer's NativeAOT publish. Without this pack item the
            // descriptor never reaches the consumer at all and the package's trim coverage
            // is inert.
            var content = EmitAndRead();
            var defaultPi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            Assert.Contains("<None Include=\"ILLink.Descriptors.xml\" Pack=\"true\"", content);
            // Exists()-guarded so a module with no open generics (no descriptor) no-ops.
            Assert.Contains("Condition=\"Exists('ILLink.Descriptors.xml')\"", content);
            // Lands in the SAME version-qualified buildTransitive/ dir as the consumer targets,
            // so $(MSBuildThisFileDirectory)ILLink.Descriptors.xml resolves beside them.
            Assert.Contains($"PackagePath=\"buildTransitive/{defaultPi.PackTfm}/\"", content);
        }

        private static string EmitAndRead()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_trim_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "ImagePipeline.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);
                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    Metadata = new XCFrameworkMetadata
                    {
                        LibraryVersion = "12.8.0",
                        PackageVersion = "12.8.0",
                        IsVersionPlaceholder = false,
                        MinimumOSVersion = "15.0",
                        EffectiveMinimumOSVersion = "15.0",
                        SdkVersion = null,
                        ModuleName = "ImagePipeline",
                        Platforms = new List<string>()
                    },
                    SourceXCFrameworkPath = sourceXcfwPath,
                }, _logger);
                return File.ReadAllText(Path.Combine(dir, "ImagePipeline.Swift.iOS.csproj"));
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    #endregion

    #region B3. Generated Documentation File Tests (Finding 55)

    /// <summary>
    /// Finding 55: the generator emits thousands of /// doc comments across the binding
    /// surface, but they never reach a packaged consumer unless the csproj emits the XML
    /// doc file (NuGet auto-includes the sibling assembly-named .xml in lib/, which is what
    /// surfaces IntelliSense downstream). The emitter must turn <c>GenerateDocumentationFile</c>
    /// on AND suppress CS1591 — doc coverage of the generated surface is partial, so the
    /// binding build must not fail over a member the generator chose not to document.
    /// </summary>
    public class BindingProjectDocumentationFileTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_GenerateDocumentationFile_Enabled()
        {
            var content = EmitAndRead();
            Assert.Contains("<GenerateDocumentationFile>true</GenerateDocumentationFile>", content);
        }

        [Fact]
        public void Emit_NoWarn_SuppressesCS1591()
        {
            // With GenerateDocumentationFile on, every public member lacking a /// comment
            // raises CS1591. Generated doc coverage is partial, so CS1591 must be suppressed
            // or the binding build breaks on the first undocumented member — turning a
            // documentation *improvement* into a build regression.
            var content = EmitAndRead();
            Assert.Contains("CS1591", content);
            Assert.Matches(@"<NoWarn>[^<]*CS1591[^<]*</NoWarn>", content);
        }

        private static string EmitAndRead()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_doc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "ImagePipeline.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);
                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    Metadata = new XCFrameworkMetadata
                    {
                        LibraryVersion = "12.8.0",
                        PackageVersion = "12.8.0",
                        IsVersionPlaceholder = false,
                        MinimumOSVersion = "15.0",
                        EffectiveMinimumOSVersion = "15.0",
                        SdkVersion = null,
                        ModuleName = "ImagePipeline",
                        Platforms = new List<string>()
                    },
                    SourceXCFrameworkPath = sourceXcfwPath,
                }, _logger);
                return File.ReadAllText(Path.Combine(dir, "ImagePipeline.Swift.iOS.csproj"));
            }
            finally { Directory.Delete(dir, true); }
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
                var content = EmitAndRead(dir, "ImagePipeline", hasWrapper: false);
                Assert.Contains("<NativeReference Include=", content);
                Assert.Contains("ImagePipeline.xcframework", content);
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
                var wrapperPath = Path.Combine(dir, "ImagePipelineSwiftBindings.xcframework");
                Directory.CreateDirectory(wrapperPath);

                var content = EmitAndRead(dir, "ImagePipeline", hasWrapper: true, wrapperPath: wrapperPath);
                Assert.Contains("ImagePipelineSwiftBindings.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithoutWrapper_NoWrapperNativeRef()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", hasWrapper: false);
                Assert.DoesNotContain("ImagePipelineSwiftBindings.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_TargetsPackItem_Present()
        {
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "ImagePipeline", hasWrapper: false);
                Assert.Contains("ImagePipeline.Swift.iOS.targets", content);
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
                var content = EmitAndRead(dir, "ImagePipeline", hasWrapper: false);
                Assert.Contains("runtimes/ios-arm64/native/ImagePipeline.xcframework/", content);
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
                var sourceXcfwPath = Path.Combine(dir, "..", "ImagePipeline.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);
                var csprojPath = Path.Combine(dir, "ImagePipeline.Swift.iOS.csproj");
                File.WriteAllText(csprojPath, "old content");

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    Metadata = CreateMinimalMetadata("ImagePipeline"),
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
                var sourceXcfwPath = Path.Combine(dir, "..", "ImagePipeline.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    Metadata = CreateMinimalMetadata("ImagePipeline"),
                    SourceXCFrameworkPath = sourceXcfwPath,
                    SwiftRuntimeVersion = "0.2.0-preview.1"
                }, _logger);

                var content = File.ReadAllText(Path.Combine(dir, "ImagePipeline.Swift.iOS.csproj"));
                Assert.Contains("0.2.0-preview.1", content);
                Assert.DoesNotContain(BindingProjectEmitter.DefaultSwiftRuntimeVersion, content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_RealSourceXcframework_SlicesAtGenerationTime()
        {
            // Real source xcframework (Info.plist on disk) → emitter slices it at generation
            // time and emits the pack <None> item against the sliced pack-staging/<rid>/ path.
            // The local NativeReference still points at the raw source so the dev-loop
            // build can pick the right slice from the full set per the Apple workload's
            // _ExpandNativeReferences logic. Skipped on non-macOS — slicer uses ditto.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

            var dir = CreateTempDir();
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "Foo.xcframework");
                CreateFakeXcframeworkWithInfoPlist(sourceXcfwPath, "Foo", new[]
                {
                    ("ios-arm64",                  "ios",   (string?)null),
                    ("ios-arm64-simulator",        "ios",   "simulator"),
                    ("macos-arm64",                "macos", (string?)null),
                    ("watchos-arm64",              "watchos", (string?)null),
                });

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "Foo",
                    Metadata = CreateMinimalMetadata("Foo"),
                    SourceXCFrameworkPath = sourceXcfwPath,
                }, _logger);

                var csproj = File.ReadAllText(Path.Combine(dir, "Foo.Swift.iOS.csproj"));

                // Pack item targets the sliced staging path, not the raw source.
                Assert.Contains("pack-staging/ios-arm64/Foo.xcframework/**", csproj.Replace('\\', '/'));
                Assert.Contains("PackagePath=\"runtimes/ios-arm64/native/Foo.xcframework/\"", csproj);

                // Local NativeReference still points at the raw source (relative to outputDir),
                // so dev-loop builds see the full slice set.
                Assert.Contains("<NativeReference Include=\"../Foo.xcframework\">", csproj);

                // Sliced output exists and contains only RID-compatible slices (no watchos/macos).
                var slicedDir = Path.Combine(dir, "pack-staging", "ios-arm64", "Foo.xcframework");
                Assert.True(Directory.Exists(slicedDir), $"sliced output missing at {slicedDir}");
                var sliceIds = Directory.EnumerateDirectories(slicedDir)
                    .Select(Path.GetFileName).OrderBy(s => s).ToList();
                Assert.Equal(new List<string?> { "ios-arm64", "ios-arm64-simulator" }, sliceIds);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void CreateFakeXcframeworkWithInfoPlist(
            string xcfwPath, string moduleName, IEnumerable<(string id, string platform, string? variant)> slices)
        {
            Directory.CreateDirectory(xcfwPath);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<plist version=\"1.0\">");
            sb.AppendLine("<dict>");
            sb.AppendLine("  <key>AvailableLibraries</key>");
            sb.AppendLine("  <array>");
            foreach (var (id, platform, variant) in slices)
            {
                sb.AppendLine("    <dict>");
                sb.AppendLine($"      <key>BinaryPath</key><string>{moduleName}.framework/{moduleName}</string>");
                sb.AppendLine($"      <key>LibraryIdentifier</key><string>{id}</string>");
                sb.AppendLine($"      <key>LibraryPath</key><string>{moduleName}.framework</string>");
                sb.AppendLine("      <key>SupportedArchitectures</key><array><string>arm64</string></array>");
                sb.AppendLine($"      <key>SupportedPlatform</key><string>{platform}</string>");
                if (variant != null)
                    sb.AppendLine($"      <key>SupportedPlatformVariant</key><string>{variant}</string>");
                sb.AppendLine("    </dict>");
                var sliceFx = Path.Combine(xcfwPath, id, $"{moduleName}.framework");
                Directory.CreateDirectory(sliceFx);
                File.WriteAllText(Path.Combine(sliceFx, moduleName), "stub-mach-o");
            }
            sb.AppendLine("  </array>");
            sb.AppendLine("  <key>CFBundlePackageType</key><string>XFWK</string>");
            sb.AppendLine("  <key>XCFrameworkFormatVersion</key><string>1.0</string>");
            sb.AppendLine("</dict>");
            sb.AppendLine("</plist>");
            File.WriteAllText(Path.Combine(xcfwPath, "Info.plist"), sb.ToString());
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
    /// Mixed framework (ONE xcframework carrying both a Swift API and an ObjC class surface)
    /// ships as a SINGLE package: the standalone-emitted Swift csproj embeds the ObjC companion's
    /// managed assembly into its own lib/ rather than depending on a separate package. The embed
    /// must (a) capture the companion's REAL built assembly via GetTargetPath — never a guessed
    /// bin.objc/$(Configuration)/$(TargetFramework)/ path that silently drifts under output-path
    /// overrides — and (b) fail closed (SWIFTBIND039) at pack if no companion assembly was captured,
    /// rather than silently shipping a Swift-only package. This mirrors the SDK's
    /// _BuildMixedObjCCompanion + SWIFTBIND039 contract for CLI/standalone publishers.
    /// </summary>
    public class BindingProjectObjCCompanionTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        private const string Companion = "ImagePipeline.ObjC.iOS.csproj";

        [Fact]
        public void Emit_MixedFramework_CompanionReferencedWithPrivateAssetsAll_NotAsDependency()
        {
            var dir = CreateTempDir();
            try
            {
                var content = Emit(dir, "ImagePipeline", Companion);
                // The companion builds via a ProjectReference, but PrivateAssets="all" stops NuGet
                // from promoting it to a package <dependency>: the assembly is EMBEDDED, not depended on.
                Assert.Contains($"<ProjectReference Include=\"{Companion}\" PrivateAssets=\"all\" />", content);
                // The ref is gated on the companion csproj existing on disk (build-order safety).
                Assert.Contains($"Condition=\"Exists('{Companion}')\"", content);
                // A BARE (promotable) ProjectReference — no PrivateAssets="all" — would make the
                // companion a separate package <dependency>: the exact topology the embed removes.
                Assert.DoesNotContain($"<ProjectReference Include=\"{Companion}\" />", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MixedFramework_CapturesCompanionViaGetTargetPath_NotGuessedPath()
        {
            var dir = CreateTempDir();
            try
            {
                var content = Emit(dir, "ImagePipeline", Companion);
                // Embed goes through a GetTargetPath capture of the REAL build output...
                Assert.Contains("Targets=\"GetTargetPath\"", content);
                Assert.Contains("ItemName=\"_SwiftBindingCompanionBuildOutput\"", content);
                Assert.Contains("<BuildOutputInPackage Include=\"@(_SwiftBindingCompanionBuildOutput)\"", content);
                // RemoveProperties keeps this project's per-TFM pack pass from cross-wiring the
                // companion's own single TFM.
                Assert.Contains("RemoveProperties=\"TargetFramework\"", content);
                // ...NOT the old guessed path, which drifts under output-path overrides.
                Assert.DoesNotContain("bin.objc/$(Configuration)/$(TargetFramework)/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_MixedFramework_FailsClosed_WhenNoCompanionCaptured()
        {
            var dir = CreateTempDir();
            try
            {
                var content = Emit(dir, "ImagePipeline", Companion);
                // SWIFTBIND039 fires when the mixed binding emitted a companion but no assembly
                // was captured to embed — a ship-blocking error, the standalone sibling of the SDK guard.
                Assert.Contains("Code=\"SWIFTBIND039\"", content);
                Assert.Contains("Condition=\"'@(_SwiftBindingCompanionBuildOutput)' == ''\"", content);
                // The fail-closed target must run unconditionally (so the Error can fire even when the
                // companion csproj is missing entirely); a Condition on the Target element would let a
                // missing companion slip through silently.
                Assert.Contains("<Target Name=\"_EmbedObjCCompanionInPackage\">", content);
                Assert.Contains("$(TargetsForTfmSpecificBuildOutput);_EmbedObjCCompanionInPackage", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SwiftOnlyFramework_NoCompanionMachinery()
        {
            var dir = CreateTempDir();
            try
            {
                // No ObjCProjectFileName → pure Swift binding → none of the embed/fail-closed machinery.
                var content = Emit(dir, "ImagePipeline", objCProjectFileName: null);
                Assert.DoesNotContain("_EmbedObjCCompanionInPackage", content);
                Assert.DoesNotContain("SWIFTBIND039", content);
                Assert.DoesNotContain("_SwiftBindingCompanionBuildOutput", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string Emit(string dir, string module, string? objCProjectFileName)
        {
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = new XCFrameworkMetadata
                {
                    LibraryVersion = "1.0.0",
                    PackageVersion = "1.0.0",
                    IsVersionPlaceholder = false,
                    MinimumOSVersion = "15.0",
                    EffectiveMinimumOSVersion = "15.0",
                    SdkVersion = null,
                    ModuleName = module,
                    Platforms = new List<string>()
                },
                SourceXCFrameworkPath = sourceXcfwPath,
                ObjCProjectFileName = objCProjectFileName,
            }, _logger);
            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_objc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Gap 2: a source framework whose native binary is a static <c>ar</c> archive is
    /// force-loaded into the wrapper, which becomes the sole runtime carrier. The csproj
    /// must therefore DROP the source xcframework NativeReference and its pack item (else
    /// the same ObjC classes are embedded/registered twice) while keeping the wrapper
    /// NativeReference. Dynamic sources (the default) keep the source reference.
    /// </summary>
    public class BindingProjectStaticLinkageTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_StaticSource_OmitsSourceNativeReferenceAndPackItem()
        {
            var dir = CreateTempDir();
            try
            {
                var wrapperPath = Path.Combine(dir, "ImagePipelineSwiftBindings.xcframework");
                Directory.CreateDirectory(wrapperPath);

                var content = Emit(dir, "ImagePipeline", wrapperPath, NativeLinkage.Static);

                // Wrapper (the sole carrier) MUST remain.
                Assert.Contains("ImagePipelineSwiftBindings.xcframework", content);
                // Source xcframework NativeReference + pack item MUST be gone.
                Assert.DoesNotContain("Include=\"../ImagePipeline.xcframework\"", content);
                Assert.DoesNotContain("runtimes/ios-arm64/native/ImagePipeline.xcframework/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_DynamicSource_KeepsSourceNativeReferenceAndPackItem()
        {
            var dir = CreateTempDir();
            try
            {
                var wrapperPath = Path.Combine(dir, "ImagePipelineSwiftBindings.xcframework");
                Directory.CreateDirectory(wrapperPath);

                var content = Emit(dir, "ImagePipeline", wrapperPath, NativeLinkage.Dynamic);

                // Dynamic source is referenced as a NativeReference and packed normally.
                Assert.Contains("Include=\"../ImagePipeline.xcframework\"", content);
                Assert.Contains("runtimes/ios-arm64/native/ImagePipeline.xcframework/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_StaticSource_WithoutWrapper_KeepsSourceAsSoleCarrier()
        {
            // --skip-wrapper-compilation (or a wrapper failure) leaves no wrapper carrier, so
            // the static source was never force-loaded into anything and is still the only
            // native. Dropping it would emit a project with no native at all — keep it.
            var dir = CreateTempDir();
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "ImagePipeline.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    Metadata = CreateMetadata("ImagePipeline"),
                    SourceXCFrameworkPath = sourceXcfwPath,
                    WrapperXCFrameworkPath = null, // no wrapper compiled
                    SourceNativeLinkage = NativeLinkage.Static,
                }, _logger);

                var content = File.ReadAllText(Path.Combine(dir, "ImagePipeline.Swift.iOS.csproj"));
                Assert.Contains("Include=\"../ImagePipeline.xcframework\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_DefaultLinkage_IsDynamic_KeepsSource()
        {
            // SourceNativeLinkage defaults to Dynamic — an emitter caller that never sets
            // it (every pre-Gap-2 call site) must keep shipping the source xcframework.
            var dir = CreateTempDir();
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "ImagePipeline.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = dir,
                    ModuleName = "ImagePipeline",
                    Metadata = CreateMetadata("ImagePipeline"),
                    SourceXCFrameworkPath = sourceXcfwPath,
                }, _logger);

                var content = File.ReadAllText(Path.Combine(dir, "ImagePipeline.Swift.iOS.csproj"));
                Assert.Contains("Include=\"../ImagePipeline.xcframework\"", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string Emit(string dir, string module, string wrapperPath, NativeLinkage linkage)
        {
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = CreateMetadata(module),
                SourceXCFrameworkPath = sourceXcfwPath,
                WrapperXCFrameworkPath = wrapperPath,
                SourceNativeLinkage = linkage,
            }, _logger);
            return File.ReadAllText(Path.Combine(dir, $"{module}.Swift.iOS.csproj"));
        }

        private static XCFrameworkMetadata CreateMetadata(string module) => new()
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
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_static_{Guid.NewGuid():N}");
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
            EffectiveMinimumOSVersion = "15.0",
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
            // ProjectReference (NOT a bare <Reference>+<HintPath>) is required so the binding
            // compiles against the in-tree Swift.Runtime managed assembly built from source and
            // picks up its runtime-flavor wiring. A raw assembly reference would bind against a
            // stale on-disk dll and miss that wiring. (The native runtime ships as a
            // SwiftBindingsRuntime.framework NativeReference, which does not propagate across a
            // ProjectReference — the deploy harness injects it separately.)
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

        [Theory]
        [InlineData("0.8.0", "[0.8.0,)")]
        [InlineData("0.10.0", "[0.10.0,)")]
        [InlineData("1.2.3", "[1.2.3,)")]
        [InlineData("0.8.0-preview.1", "[0.8.0-preview.1,)")]
        public void BuildMinimumOnly_EmitsFloorOnlyRange(string version, string expected)
        {
            // Pin the floor-only shape used by the Apple supplement nuspec's outbound Runtime
            // dep. The supplement is always brokered by the SDK (whose own bounded Runtime
            // PackageReference is the actual contract), so the supplement only needs to
            // declare a floor — letting one shipped supplement nupkg ride forward across
            // Runtime/SDK minor bumps without a no-op repack.
            Assert.Equal(expected, RuntimeVersionRange.BuildMinimumOnly(version));
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("1")]
        [InlineData("not.a.semver")]
        [InlineData("x.8.0")]
        [InlineData(".8.0")]
        public void BuildMinimumOnly_FallsBackOnUnparseableInput(string version)
        {
            // Same defensive contract as Build: if either major or minor is non-integer,
            // return the raw input rather than emitting a malformed range. The floor-only
            // form has no upper bound that could go wrong, but we still gate on parse to
            // keep the helper's failure mode symmetric and predictable.
            Assert.Equal(version, RuntimeVersionRange.BuildMinimumOnly(version));
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
                        EffectiveMinimumOSVersion = "15.0",
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
                var content = EmitAndRead(dir, "ImagePipeline", dependencies: null);
                // Should have only the Swift.Runtime PackageReference
                Assert.Contains("SwiftBindings.Runtime", content);
                Assert.DoesNotContain("SmartCardLib", content);
                Assert.DoesNotContain("PaymentSdkCore", content);
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
                        XCFrameworkPath = "/path/to/SmartCardLib.xcframework",
                        ModuleName = "SmartCardLib",
                        PackageVersion = "1.2.0"
                    }
                };
                var content = EmitAndRead(dir, "ACSSmartCardLib", dependencies: deps);
                Assert.Contains("<PackageReference Include=\"SmartCardLib.Swift.iOS\" Version=\"1.2.0\" />", content);
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
                        XCFrameworkPath = "/path/to/PaymentSdkCore.xcframework",
                        ModuleName = "PaymentSdkCore",
                        PackageVersion = "24.0.0"
                    },
                    new()
                    {
                        XCFrameworkPath = "/path/to/PaymentSdkUICore.xcframework",
                        ModuleName = "PaymentSdkUICore",
                        PackageVersion = "24.0.0"
                    }
                };
                var content = EmitAndRead(dir, "PaymentSdkSheet", dependencies: deps);
                Assert.Contains("PaymentSdkCore.Swift.iOS", content);
                Assert.Contains("PaymentSdkUICore.Swift.iOS", content);
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
                        XCFrameworkPath = "/path/to/PaymentSdk3DS2.xcframework",
                        ModuleName = "PaymentSdk3DS2",
                        IsObjCOnly = true
                    }
                };
                var content = EmitAndRead(dir, "PaymentSdkPayments", dependencies: deps);
                // Should have Swift.Runtime but NOT the ObjC-only dep
                Assert.Contains("SwiftBindings.Runtime", content);
                Assert.DoesNotContain("PaymentSdk3DS2", content);
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
                        XCFrameworkPath = "/path/to/PaymentSdkCore.xcframework",
                        ModuleName = "PaymentSdkCore",
                        PackageVersion = "24.0.0",
                        IsObjCOnly = false
                    },
                    new()
                    {
                        XCFrameworkPath = "/path/to/PaymentSdk3DS2.xcframework",
                        ModuleName = "PaymentSdk3DS2",
                        IsObjCOnly = true
                    }
                };
                var content = EmitAndRead(dir, "PaymentSdkPayments", dependencies: deps);
                Assert.Contains("PaymentSdkCore.Swift.iOS", content);
                Assert.DoesNotContain("PaymentSdk3DS2", content);
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
                var bridgePath = Path.Combine(dir, "ImagePipelineBridge.xcframework");
                Directory.CreateDirectory(bridgePath);

                var content = EmitAndRead(dir, "ImagePipeline", bridgePath: bridgePath);
                Assert.Contains("ImagePipelineBridge.xcframework", content);
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
                var content = EmitAndRead(dir, "ImagePipeline", bridgePath: null);
                Assert.DoesNotContain("ImagePipelineBridge.xcframework", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithBridge_BridgePackItemPresent()
        {
            var dir = CreateTempDir();
            try
            {
                var bridgePath = Path.Combine(dir, "ImagePipelineBridge.xcframework");
                Directory.CreateDirectory(bridgePath);

                var content = EmitAndRead(dir, "ImagePipeline", bridgePath: bridgePath);
                Assert.Contains("ImagePipelineBridge.xcframework/**", content);
                Assert.Contains("runtimes/ios-arm64/native/ImagePipelineBridge.xcframework/", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_BridgeAndWrapper_BothPresent()
        {
            var dir = CreateTempDir();
            try
            {
                var wrapperPath = Path.Combine(dir, "ImagePipelineSwiftBindings.xcframework");
                Directory.CreateDirectory(wrapperPath);
                var bridgePath = Path.Combine(dir, "ImagePipelineBridge.xcframework");
                Directory.CreateDirectory(bridgePath);

                var content = EmitAndRead(dir, "ImagePipeline", wrapperPath: wrapperPath, bridgePath: bridgePath);
                Assert.Contains("ImagePipelineSwiftBindings.xcframework", content);
                Assert.Contains("ImagePipelineBridge.xcframework", content);
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
                var content = EmitAndRead(dir, "ImagePipeline", hasBridgeSwift: true);
                Assert.Contains("ImagePipelineBridge.xcframework", content);
                // Exists() guard (activates after bridge compile) AND native-macOS
                // exclusion (UIKit-only bridge → empty macOS slice → MT158).
                Assert.Contains("Exists('ImagePipelineBridge.xcframework') AND !$(TargetFramework.Contains('-macos'))", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_BridgeNativeRefAndPack_ExcludedOnNativeMacOS()
        {
            // The SwiftUI bridge is UIKit-only: its macOS slice is an empty Mach-O
            // that fails a native-macOS consumer with Xamarin MT158. Both the bridge
            // NativeReference and its pack item must carry the !Contains('-macos')
            // gate (mirroring the .SwiftUIBridge.cs Compile gate). Mac Catalyst keeps
            // the bridge — net*-maccatalyst does not contain "-macos".
            var dir = CreateTempDir();
            try
            {
                var bridgePath = Path.Combine(dir, "ImagePipelineBridge.xcframework");
                Directory.CreateDirectory(bridgePath);

                var content = EmitAndRead(dir, "ImagePipeline", bridgePath: bridgePath);
                // Tie the !-macos gate to each bridge element specifically — a loose
                // "gate string present somewhere" assert would pass even if the gate
                // landed on the wrong item. The NativeReference Condition:
                Assert.Matches(
                    @"<NativeReference Include=""ImagePipelineBridge\.xcframework""\s+Condition=""Exists\('ImagePipelineBridge\.xcframework'\) AND !\$\(TargetFramework\.Contains\('-macos'\)\)"">",
                    content);
                // The pack <None Pack="true"> item Condition:
                Assert.Matches(
                    @"<None Include=""ImagePipelineBridge\.xcframework/\*\*"" Pack=""true""\s+Condition=""Exists\('ImagePipelineBridge\.xcframework'\) AND !\$\(TargetFramework\.Contains\('-macos'\)\)""",
                    content);
                // The two bridge items are the only places the macOS gate attaches to a
                // .xcframework reference; the wrapper must remain unconditional on macOS.
                Assert.DoesNotContain("ImagePipelineSwiftBindings.xcframework') AND !$(TargetFramework.Contains('-macos'))", content);
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

    #region G. Apple Supplement Reference Tests

    public class BindingProjectAppleSupplementTests
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_WithoutSupplementReference_NoAppleSupplementPackageRef()
        {
            // Non-Apple consumer: generator did not resolve any Swift-only Apple type,
            // so the csproj must not pick up SwiftBindings.Apple.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "VectorAnimation", emitsAppleSupplementRef: false);
                Assert.DoesNotContain("SwiftBindings.Apple", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithSupplementReference_AddsOpenEndedPackageReference()
        {
            // Positive case: at least one Apple-supplement type resolved during emission.
            // Expect an open-ended version (the supplement is cross-major additive-only so
            // consumers must be free to float forward onto a newer SDK train).
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(dir, "Translation", emitsAppleSupplementRef: true);
                Assert.Contains(
                    "<PackageReference Include=\"SwiftBindings.Apple\" Version=\"[26.0.0,)\" />",
                    content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithSupplementVersionOverride_FlowsThrough()
        {
            // Override hook for callers that need a different floor than the default.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(
                    dir, "FamilyControls",
                    emitsAppleSupplementRef: true, supplementVersion: "19.2.0");
                Assert.Contains(
                    "<PackageReference Include=\"SwiftBindings.Apple\" Version=\"[19.2.0,)\" />",
                    content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithSupplementRefAndNullVersion_ThrowsMeaningfulException()
        {
            // Regression guard: the old hardcoded "26.0.0" default silently shipped
            // a stale supplement version on Apple SDK train bumps if a caller
            // forgot to thread --apple-version through. Emit must fail loudly.
            var dir = CreateTempDir();
            try
            {
                var sourceXcfwPath = Path.Combine(dir, "..", "Translation.xcframework");
                Directory.CreateDirectory(sourceXcfwPath);
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                    {
                        OutputDirectory = dir,
                        ModuleName = "Translation",
                        Metadata = CreateMinimalMetadata("Translation"),
                        SourceXCFrameworkPath = sourceXcfwPath,
                        EmitsAppleSupplementReference = true,
                        AppleSupplementVersion = null,
                    }, _logger));
                Assert.Contains("AppleSupplementVersion", ex.Message);
                Assert.Contains("--apple-version", ex.Message);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_WithPrototypePath_UsesProjectReferenceInsteadOfPackage()
        {
            // Prototype mode wins over PackageReference so canonical identity stays stable
            // when the SDK materializes a supplement project into obj/.
            var dir = CreateTempDir();
            try
            {
                var content = EmitAndRead(
                    dir, "Translation",
                    emitsAppleSupplementRef: true,
                    prototypePath: "obj/generated/SwiftBindings.Apple.Prototype.csproj");
                Assert.DoesNotContain(
                    "<PackageReference Include=\"SwiftBindings.Apple\"",
                    content);
                Assert.Contains(
                    "<ProjectReference Include=\"obj/generated/SwiftBindings.Apple.Prototype.csproj\" />",
                    content);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string EmitAndRead(
            string dir, string module,
            bool emitsAppleSupplementRef,
            string? supplementVersion = null,
            string? prototypePath = null)
        {
            var sourceXcfwPath = Path.Combine(dir, "..", $"{module}.xcframework");
            Directory.CreateDirectory(sourceXcfwPath);

            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
            {
                OutputDirectory = dir,
                ModuleName = module,
                Metadata = CreateMinimalMetadata(module),
                SourceXCFrameworkPath = sourceXcfwPath,
                EmitsAppleSupplementReference = emitsAppleSupplementRef,
                AppleSupplementVersion = supplementVersion ?? "26.0.0",
                AppleSupplementPrototypeProjectPath = prototypePath,
            }, _logger);
            var defaultPi = PlatformInfoFactory.Create(ApplePlatform.iOS);
            var packageId = defaultPi.GetDefaultSwiftPackageId(module);
            return File.ReadAllText(Path.Combine(dir, $"{packageId}.csproj"));
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
            var dir = Path.Combine(Path.GetTempPath(), $"bpe_supplement_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region H. Apple Supplement Resolver / Reference Tracker Tests

    /// <summary>
    /// Per-test reset for the <c>[ThreadStatic]</c> <see cref="AppleSupplementReferences"/>
    /// set. xUnit instantiates one test-class object per test, so <c>InitializeAsync</c>
    /// clears any leftover identities recorded by a prior test that ran on the same
    /// threadpool thread; <c>DisposeAsync</c> scrubs after we finish to avoid leaking
    /// out of the suite. Without this, a test that forgets a manual <c>Reset()</c> can
    /// contaminate the next one's view of <c>AppleSupplementReferences.Any</c>.
    /// </summary>
    public abstract class AppleSupplementReferencesTestBase : IAsyncLifetime
    {
        public Task InitializeAsync()
        {
            AppleSupplementReferences.Reset();
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            AppleSupplementReferences.Reset();
            return Task.CompletedTask;
        }
    }

    public class AppleSupplementResolverTests : AppleSupplementReferencesTestBase
    {
        [Fact]
        public void Resolver_KnownManifestType_ProducesSyntheticRecord()
        {
            // Foundation.Locale.Language ships in the seeded manifest and resolves via the
            // Apple supplement. The synthetic TypeRecord's C# projection mirrors the
            // supplement's emitted type identity (Swift.Foundation.Locale.Language).
            var swiftName = SwiftTypeName.FromModuleQualifiedName("Foundation.Locale.Language");
            var found = AppleSupplementResolver.TryResolve(
                swiftName, currentlyGeneratingModule: null, out var record);

            Assert.True(found);
            Assert.Equal("Swift.Foundation.Locale", record.CSharpTypeName.Namespace);
            Assert.Equal("Language", record.CSharpTypeName.Name);
            Assert.Equal(TypeRecordKind.Struct, record.Kind);
        }

        [Fact]
        public void Resolver_FoundationAttributedString_ResolvesAsSwiftFoundationSupplement()
        {
            // Foundation.AttributedString ships in include-types.json + manifest.json with a
            // VWT-opaque storage strategy and Swift.Foundation namespace projection. Without a
            // dedicated test, the entry could silently disappear from include-types.json (the
            // build would still pass — Microsoft.iOS exposes NSAttributedString, so emission
            // falls back to an ObjC handle that compiles but breaks at runtime when callers
            // hand in a Swift-side AttributedString). This pins the resolver result so the
            // entry can't regress to that broken-but-compilable shape unnoticed.
            var swiftName = SwiftTypeName.FromModuleQualifiedName("Foundation.AttributedString");
            var found = AppleSupplementResolver.TryResolve(
                swiftName, currentlyGeneratingModule: null, out var record);

            Assert.True(found);
            Assert.Equal("Swift.Foundation", record.CSharpTypeName.Namespace);
            Assert.Equal("AttributedString", record.CSharpTypeName.Name);
            Assert.Equal(TypeRecordKind.Struct, record.Kind);
            Assert.True(record.Flags.HasFlag(TypeRecordFlags.RequiresMemoryManagement));
            Assert.False(record.Flags.HasFlag(TypeRecordFlags.Frozen));
        }

        [Fact]
        public void Resolver_LegacyRuntimeCanonical_DoesNotHijack()
        {
            // Foundation.Date is a legacy canonical pinned to SwiftBindings.Runtime via the
            // per-type override. The supplement resolver must stay silent so the primary
            // TypeDatabase entry (the hand-rolled Swift.Date) continues to win.
            var swiftName = SwiftTypeName.FromModuleQualifiedName("Foundation.Date");
            var found = AppleSupplementResolver.TryResolve(
                swiftName, currentlyGeneratingModule: null, out _);

            Assert.False(found);
        }

        [Fact]
        public void Resolver_UnknownType_ReturnsFalse()
        {
            // An arbitrary third-party Swift identity must not accidentally route through
            // the supplement resolver — otherwise every non-Apple consumer would silently
            // grow a SwiftBindings.Apple PackageReference.
            var swiftName = SwiftTypeName.FromModuleQualifiedName("VectorAnimation.VectorAnimationAsset");
            var found = AppleSupplementResolver.TryResolve(
                swiftName, currentlyGeneratingModule: null, out _);

            Assert.False(found);
        }

        [Fact]
        public void ReferenceTracker_RoundTrip_RecordsAndResets()
        {
            // InitializeAsync already reset the tracker; this test is about validating
            // the Record/Reset round-trip behaviour itself.
            Assert.False(AppleSupplementReferences.Any);

            AppleSupplementReferences.Record("Foundation.Locale.Language");
            AppleSupplementReferences.Record("Foundation.Locale.Language"); // dedup
            AppleSupplementReferences.Record("CryptoKit.P256.Signing.ECDSASignature");
            Assert.True(AppleSupplementReferences.Any);
            Assert.Equal(2, AppleSupplementReferences.Current.Count);

            AppleSupplementReferences.Reset();
            Assert.False(AppleSupplementReferences.Any);
        }
    }

    #endregion

    #region I. Apple Supplement Prototype Emitter Tests

    public class AppleSupplementPrototypeEmitterTests : AppleSupplementReferencesTestBase
    {
        private static readonly ILogger _logger = NullLogger.Instance;

        [Fact]
        public void Emit_MaterializesCsprojAndSourceFile()
        {
            // Happy path: one known manifest identity → csproj + .cs source on disk.
            var dir = CreateTempDir();
            try
            {
                var result = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                {
                    PrototypeDirectory = dir,
                    ReferencedIdentities = new[] { "Foundation.Locale.Language" },
                    PlatformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS),
                    SwiftRuntimeVersion = "0.8.0",
                }, _logger);

                Assert.True(File.Exists(result.CsprojPath));
                Assert.EndsWith("SwiftBindings.Apple.Prototype.csproj", result.CsprojPath);
                Assert.NotEmpty(result.EmittedSourceFiles);
                Assert.All(result.EmittedSourceFiles, f => Assert.True(File.Exists(f)));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_CsprojPinsCanonicalAssemblyIdentity()
        {
            // Prototype csproj MUST emit AssemblyName=SwiftBindings.Apple + RootNamespace=Swift
            // so the generated consumer bindings resolve to the same symbols they would hit
            // through a PackageReference. Drift here silently forks the supplement surface.
            var dir = CreateTempDir();
            try
            {
                var result = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                {
                    PrototypeDirectory = dir,
                    ReferencedIdentities = new[] { "Foundation.Locale.Language" },
                    PlatformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS),
                    SwiftRuntimeVersion = "0.8.0",
                }, _logger);
                var csproj = File.ReadAllText(result.CsprojPath);

                Assert.Contains("<AssemblyName>SwiftBindings.Apple</AssemblyName>", csproj);
                Assert.Contains("<RootNamespace>Swift</RootNamespace>", csproj);
                Assert.Contains("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>", csproj);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_CsprojListsEmittedSourcesAsCompileItems()
        {
            var dir = CreateTempDir();
            try
            {
                var result = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                {
                    PrototypeDirectory = dir,
                    ReferencedIdentities = new[] { "Foundation.Locale.Language" },
                    PlatformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS),
                    SwiftRuntimeVersion = "0.8.0",
                }, _logger);
                var csproj = File.ReadAllText(result.CsprojPath);

                foreach (var emitted in result.EmittedSourceFiles)
                {
                    var rel = Path.GetRelativePath(dir, emitted).Replace('\\', '/');
                    Assert.Contains($"<Compile Include=\"{rel}\" />", csproj);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_UsesBoundedRuntimeVersionRangeForPublishedRuntime()
        {
            var dir = CreateTempDir();
            try
            {
                var result = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                {
                    PrototypeDirectory = dir,
                    ReferencedIdentities = new[] { "Foundation.Locale.Language" },
                    PlatformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS),
                    SwiftRuntimeVersion = "0.8.0",
                }, _logger);
                var csproj = File.ReadAllText(result.CsprojPath);

                Assert.Contains(
                    "<PackageReference Include=\"SwiftBindings.Runtime\" Version=\"[0.8.0,0.9.0)\" />",
                    csproj);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_DevSentinelEmitsInTreeProjectReference()
        {
            var dir = CreateTempDir();
            try
            {
                var result = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                {
                    PrototypeDirectory = dir,
                    ReferencedIdentities = new[] { "Foundation.Locale.Language" },
                    PlatformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS),
                    SwiftRuntimeVersion = null,
                }, _logger);
                var csproj = File.ReadAllText(result.CsprojPath);

                Assert.Contains("$(SwiftBindingsRepoRoot)/src/Swift.Runtime/src/Swift.Runtime.csproj", csproj);
                Assert.Contains("[0.0.0-dev]", csproj);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_OnlyReferencedIdentitiesAppear()
        {
            // Trimming guarantee: even though the manifest carries many types, only the
            // identities we asked for should produce .cs files. The registration side-car
            // (`_AppleSupplementRegistration.cs`) is always emitted and is exempt from the
            // trimming guarantee — it's a constant resolver-wiring file, not a type.
            var dir = CreateTempDir();
            try
            {
                var result = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                {
                    PrototypeDirectory = dir,
                    ReferencedIdentities = new[] { "Foundation.Locale.Language" },
                    PlatformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS),
                    SwiftRuntimeVersion = "0.8.0",
                }, _logger);

                var typeFiles = result.EmittedSourceFiles
                    .Where(f => !f.EndsWith("_AppleSupplementRegistration.cs", StringComparison.Ordinal))
                    .ToList();
                Assert.NotEmpty(typeFiles);
                foreach (var emitted in typeFiles)
                    Assert.Contains("Locale.Language.cs", emitted);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_EmptyIdentitySet_Throws()
        {
            // Empty set is a caller bug — the wiring in BindingsGeneratorCommand gates on
            // AppleSupplementReferences.Any specifically so we never reach the emitter
            // with nothing to write. Fail loud if that invariant breaks.
            var dir = CreateTempDir();
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                    {
                        PrototypeDirectory = dir,
                        ReferencedIdentities = Array.Empty<string>(),
                        PlatformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS),
                    }, _logger));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Emit_SecondRun_CleansStaleSources()
        {
            // Fresh-emit: shrinking the identity set must remove sources a prior run left behind.
            // The registration side-car (`_AppleSupplementRegistration.cs`) is always re-emitted,
            // so we compare on type files only.
            var dir = CreateTempDir();
            try
            {
                var first = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                {
                    PrototypeDirectory = dir,
                    ReferencedIdentities = new[] { "Foundation.Locale.Language" },
                    PlatformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS),
                    SwiftRuntimeVersion = "0.8.0",
                }, _logger);
                var firstTypeFiles = first.EmittedSourceFiles
                    .Where(f => !f.EndsWith("_AppleSupplementRegistration.cs", StringComparison.Ordinal))
                    .ToList();
                Assert.NotEmpty(firstTypeFiles);

                // Second invocation with an unknown identity — manifest trimmer drops it, so
                // the emitted type set shrinks to zero and the prior run's type file must be gone.
                var second = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                {
                    PrototypeDirectory = dir,
                    ReferencedIdentities = new[] { "Foundation.NotARealType.At.All" },
                    PlatformInfo = PlatformInfoFactory.Create(ApplePlatform.iOS),
                    SwiftRuntimeVersion = "0.8.0",
                }, _logger);
                var secondTypeFiles = second.EmittedSourceFiles
                    .Where(f => !f.EndsWith("_AppleSupplementRegistration.cs", StringComparison.Ordinal))
                    .ToList();
                Assert.Empty(secondTypeFiles);
                foreach (var stale in firstTypeFiles)
                    Assert.False(File.Exists(stale), $"Stale type file lingered: {stale}");
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"asp_emitter_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion
}
