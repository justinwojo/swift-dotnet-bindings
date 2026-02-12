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
                    metadata, dir, true, "NukeSwiftBindings", 2, _logger);

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
                    metadata, dir, true, "NukeSwiftBindings", 2, _logger);

                var doc = new XmlDocument();
                doc.Load(Path.Combine(dir, "binding-metadata.props"));

                var ns = new XmlNamespaceManager(doc.NameTable);
                var root = doc.DocumentElement!;

                AssertPropertyValue(root, "_SwiftBindingPackageVersion", "12.8.0");
                AssertPropertyValue(root, "_SwiftBindingMinimumOSVersion", "15.0");
                AssertPropertyValue(root, "_SwiftBindingModuleName", "Nuke");
                AssertPropertyValue(root, "_SwiftBindingIsVersionPlaceholder", "False");
                AssertPropertyValue(root, "_SwiftBindingHasWrapperXCFramework", "True");
                AssertPropertyValue(root, "_SwiftBindingWrapperModuleName", "NukeSwiftBindings");
                AssertPropertyValue(root, "_SwiftBindingWrapperSliceCount", "2");
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
                    metadata, dir, true, "NukeSwiftBindings", 1, _logger);

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
            ModuleName = "Nuke",
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
}
