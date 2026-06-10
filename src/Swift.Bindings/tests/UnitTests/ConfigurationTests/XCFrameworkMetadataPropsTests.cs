// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests
{
    #region E. Metadata Props Emission Tests

    public class MetadataPropsEmissionTests
    {
        private static readonly Microsoft.Extensions.Logging.ILogger _logger = NullLogger.Instance;

        [Fact]
        public void EmitMetadataProps_CreatesFile()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger);

                Assert.True(File.Exists(Path.Combine(dir, "binding-metadata.props")));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_ContainsAllProperties()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));

                var ns = new XmlNamespaceManager(doc.NameTable);
                var root = doc.DocumentElement!;

                AssertPropertyValue(root, "_SwiftBindingPackageVersion", "12.8.0");
                AssertPropertyValue(root, "_SwiftBindingMinimumOSVersion", "15.0");
                AssertPropertyValue(root, "_SwiftBindingModuleName", "ImagePipeline");
                AssertPropertyValue(root, "_SwiftBindingIsVersionPlaceholder", "False");
                AssertPropertyValue(root, "_SwiftBindingHasWrapperXCFramework", "True");
                AssertPropertyValue(root, "_SwiftBindingWrapperModuleName", "ImagePipelineSwiftBindings");
                AssertPropertyValue(root, "_SwiftBindingWrapperSliceCount", "2");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_DefaultsSourceNativeLinkage_ToDynamic()
        {
            // The common case: a dynamic source framework. Absent an explicit Static
            // classification, the consumer keeps its source xcframework reference.
            var dir = CreateTempDir();
            try
            {
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    CreateTestMetadata(), dir, true, "ImagePipelineSwiftBindings", 2, _logger);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                AssertPropertyValue(doc.DocumentElement!, "_SwiftBindingSourceNativeLinkage", "Dynamic");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_StaticSourceNativeLinkage_EmitsStatic()
        {
            // Gap 2: a static-archive source emits Static so the SDK drops the source
            // xcframework reference (the wrapper is the sole carrier).
            var dir = CreateTempDir();
            try
            {
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    CreateTestMetadata(), dir, true, "ImagePipelineSwiftBindings", 2, _logger,
                    sourceNativeLinkage: NativeLinkage.Static);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                AssertPropertyValue(doc.DocumentElement!, "_SwiftBindingSourceNativeLinkage", "Static");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_NoWrapper_ReflectsState()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = new XCFrameworkMetadata
                {
                    LibraryVersion = "1.0",
                    PackageVersion = "0.0.0",
                    IsVersionPlaceholder = true,
                    MinimumOSVersion = "15.0",
                    EffectiveMinimumOSVersion = "15.0",
                    SdkVersion = null,
                    ModuleName = "TestLib",
                    Platforms = new List<string>()
                };

                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, false, "TestLibSwiftBindings", 0, _logger);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                AssertPropertyValue(root, "_SwiftBindingPackageVersion", "0.0.0");
                AssertPropertyValue(root, "_SwiftBindingIsVersionPlaceholder", "True");
                AssertPropertyValue(root, "_SwiftBindingHasWrapperXCFramework", "False");
                AssertPropertyValue(root, "_SwiftBindingWrapperSliceCount", "0");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_IsValidMSBuildXml()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 1, _logger);

                var content = File.ReadAllText(Path.Combine(dir, "binding-metadata.props"));
                Assert.StartsWith("<Project>", content.TrimStart());

                // Verify it parses as valid XML
                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                Assert.Equal("Project", doc.DocumentElement!.Name);
                Assert.NotNull(doc.SelectSingleNode("//PropertyGroup"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_WithDependencies_IncludesDependencyProperty()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                var deps = new List<FrameworkDependencyInfo>
                {
                    new() { XCFrameworkPath = "/path/to/PaymentSdkCore.xcframework", ModuleName = "PaymentSdkCore", PackageVersion = "25.6.2" },
                    new() { XCFrameworkPath = "/path/to/PaymentSdkUICore.xcframework", ModuleName = "PaymentSdkUICore" }
                };

                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger, deps);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                var node = root.SelectSingleNode("//PropertyGroup/_SwiftBindingDependencies");
                Assert.NotNull(node);
                var value = node!.InnerText;
                Assert.Contains("PaymentSdkCore|PaymentSdkCore.Swift.iOS|25.6.2|/path/to/PaymentSdkCore.xcframework", value);
                Assert.Contains("PaymentSdkUICore|PaymentSdkUICore.Swift.iOS|0.0.0|/path/to/PaymentSdkUICore.xcframework", value);
                // Entries are semicolon-delimited
                Assert.Contains(";", value);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_NoDependencies_OmitsDependencyProperty()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();

                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                var node = root.SelectSingleNode("//PropertyGroup/_SwiftBindingDependencies");
                Assert.Null(node);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_FiltersObjCOnlyDependencies()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                var deps = new List<FrameworkDependencyInfo>
                {
                    new() { XCFrameworkPath = "/path/to/PaymentSdkCore.xcframework", ModuleName = "PaymentSdkCore", PackageVersion = "25.6.2" },
                    new() { XCFrameworkPath = "/path/to/ObjCLib.xcframework", ModuleName = "ObjCLib", IsObjCOnly = true }
                };

                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger, deps);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                var node = root.SelectSingleNode("//PropertyGroup/_SwiftBindingDependencies");
                Assert.NotNull(node);
                var value = node!.InnerText;
                Assert.Contains("PaymentSdkCore", value);
                Assert.DoesNotContain("ObjCLib", value);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_DependencyFormat_IncludesXCFrameworkPath()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/abs/path/PaymentSdkCore.xcframework",
                        ModuleName = "PaymentSdkCore",
                        PackageVersion = "25.6.2",
                        PackageId = "Custom.PaymentSdkCore"
                    }
                };

                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger, deps);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                var node = root.SelectSingleNode("//PropertyGroup/_SwiftBindingDependencies");
                Assert.NotNull(node);
                // Format: ModuleName|PackageId|Version|XCFrameworkPath
                Assert.Equal("PaymentSdkCore|Custom.PaymentSdkCore|25.6.2|/abs/path/PaymentSdkCore.xcframework", node!.InnerText);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_XmlSpecialCharsInPath_ProducesValidXml()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/Users/dev/R&D/Libs<2>/Core.xcframework",
                        ModuleName = "Core",
                        PackageVersion = "1.0.0"
                    }
                };

                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger, deps);

                // Must parse as valid XML (would throw if & or < are unescaped)
                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                var node = root.SelectSingleNode("//PropertyGroup/_SwiftBindingDependencies");
                Assert.NotNull(node);
                // XmlDocument.InnerText returns XML-decoded text; delimiter encoding (%7C, %3B, %25)
                // is transparent here since the path has no |, ;, or % characters.
                // The original path round-trips through XML escaping.
                Assert.Contains("/Users/dev/R&D/Libs<2>/Core.xcframework", node!.InnerText);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_DelimiterCharsInPath_ArePercentEncoded()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/Users/dev/path;with|delimiters/Core.xcframework",
                        ModuleName = "Core",
                        PackageVersion = "1.0.0"
                    }
                };

                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger, deps);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                var node = root.SelectSingleNode("//PropertyGroup/_SwiftBindingDependencies");
                Assert.NotNull(node);
                var text = node!.InnerText;

                // | and ; in path are percent-encoded so they don't corrupt the delimiter format
                Assert.Contains("%7C", text);
                Assert.Contains("%3B", text);
                // The encoded path should be in the 4th field (after 3 literal | delimiters)
                var fields = text.Split('|');
                Assert.Equal(4, fields.Length);
                Assert.Equal("/Users/dev/path%3Bwith%7Cdelimiters/Core.xcframework", fields[3]);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_PercentInPath_DoesNotDoubleEncode()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                var deps = new List<FrameworkDependencyInfo>
                {
                    new()
                    {
                        XCFrameworkPath = "/Users/dev/100%done/Core.xcframework",
                        ModuleName = "Core",
                        PackageVersion = "1.0.0"
                    }
                };

                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger, deps);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                var node = root.SelectSingleNode("//PropertyGroup/_SwiftBindingDependencies");
                Assert.NotNull(node);
                var fields = node!.InnerText.Split('|');
                // % is encoded as %25 so decoding won't corrupt %7C/%3B sequences
                Assert.Equal("/Users/dev/100%25done/Core.xcframework", fields[3]);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void AssertPropertyValue(XmlElement root, string propertyName, string expectedValue)
        {
            var node = root.SelectSingleNode($"//PropertyGroup/{propertyName}");
            Assert.NotNull(node);
            Assert.Equal(expectedValue, node!.InnerText);
        }

        private static XCFrameworkMetadata CreateTestMetadata() => new()
        {
            LibraryVersion = "12.8.0",
            PackageVersion = "12.8.0",
            IsVersionPlaceholder = false,
            MinimumOSVersion = "13.0",
            EffectiveMinimumOSVersion = "15.0",
            SdkVersion = "18.0",
            ModuleName = "ImagePipeline",
            Platforms = new List<string> { "ios-simulator", "ios" }
        };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"meta_props_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion

    #region F. Bridge Metadata Props Tests

    public class BridgeMetadataPropsTests
    {
        private static readonly Microsoft.Extensions.Logging.ILogger _logger = NullLogger.Instance;

        [Fact]
        public void EmitMetadataProps_WithBridge_ContainsBridgeProperties()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger,
                    hasBridgeSwift: true, bridgeModuleName: "ImagePipelineBridge");

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                AssertPropertyValue(root, "_SwiftBindingHasBridgeSwift", "True");
                AssertPropertyValue(root, "_SwiftBindingBridgeModuleName", "ImagePipelineBridge");
                AssertPropertyValue(root, "_SwiftBindingHasBridgeXCFramework", "False");
                AssertPropertyValue(root, "_SwiftBindingBridgeSliceCount", "0");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_NoBridge_OmitsBridgeProperties()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger);

                var content = File.ReadAllText(Path.Combine(dir, "binding-metadata.props"));
                Assert.DoesNotContain("_SwiftBindingHasBridgeSwift", content);
                Assert.DoesNotContain("_SwiftBindingBridgeModuleName", content);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void EmitMetadataProps_WithBridge_DefaultModuleName()
        {
            var dir = CreateTempDir();
            try
            {
                var metadata = CreateTestMetadata();
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger,
                    hasBridgeSwift: true);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                // Default bridge module name: {ModuleName}Bridge
                AssertPropertyValue(root, "_SwiftBindingBridgeModuleName", "ImagePipelineBridge");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void UpdateMetadataPropsBridgeStatus_SetsProperties()
        {
            var dir = CreateTempDir();
            try
            {
                // First emit props with bridge
                var metadata = CreateTestMetadata();
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, dir, true, "ImagePipelineSwiftBindings", 2, _logger,
                    hasBridgeSwift: true, bridgeModuleName: "ImagePipelineBridge");

                // Now update bridge status
                XCFrameworkMetadataExtractor.UpdateMetadataPropsBridgeStatus(
                    dir, true, "ImagePipelineBridge", 2, _logger);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));
                var root = doc.DocumentElement!;

                AssertPropertyValue(root, "_SwiftBindingHasBridgeXCFramework", "True");
                AssertPropertyValue(root, "_SwiftBindingBridgeSliceCount", "2");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void UpdateMetadataPropsBridgeStatus_NoFile_DoesNotThrow()
        {
            var dir = CreateTempDir();
            try
            {
                // Should log warning but not throw
                XCFrameworkMetadataExtractor.UpdateMetadataPropsBridgeStatus(
                    dir, true, "ImagePipelineBridge", 2, _logger);
            }
            finally { Directory.Delete(dir, true); }
        }

        private static void AssertPropertyValue(XmlElement root, string propertyName, string expectedValue)
        {
            var node = root.SelectSingleNode($"//PropertyGroup/{propertyName}");
            Assert.NotNull(node);
            Assert.Equal(expectedValue, node!.InnerText);
        }

        private static XCFrameworkMetadata CreateTestMetadata() => new()
        {
            LibraryVersion = "12.8.0",
            PackageVersion = "12.8.0",
            IsVersionPlaceholder = false,
            MinimumOSVersion = "13.0",
            EffectiveMinimumOSVersion = "15.0",
            SdkVersion = "18.0",
            ModuleName = "ImagePipeline",
            Platforms = new List<string> { "ios-simulator", "ios" }
        };

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"bridge_props_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    #endregion
}
