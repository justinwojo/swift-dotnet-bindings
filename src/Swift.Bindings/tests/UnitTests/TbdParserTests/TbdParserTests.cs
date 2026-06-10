// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using TbdParsing;
using TbdParsing.Models;
using Xunit;

namespace BindingsGeneration.Tests
{
    public class TbdParserTests : IClassFixture<TbdParserTests.TestFixture>
    {
        private readonly TestFixture _fixture;
        private static string _tbdFilePath;
        private static TbdParser _tbdParser;
        private static string _mockTbdFilePath;

        public TbdParserTests(TestFixture fixture)
        {
            _fixture = fixture;
        }

        public class TestFixture : IDisposable
        {
            static TestFixture()
            {
                InitializeResources();
            }

            private static void InitializeResources()
            {
                _tbdFilePath = "/Applications/Xcode.app/Contents/Developer/Platforms/MacOSX.platform/Developer/SDKs/MacOSX.sdk/System/Library/Frameworks/Foundation.framework/Foundation.tbd";
                _tbdParser = new TbdParser(NullLoggerFactory.Instance);

                // Create a mock TBD file for testing
                _mockTbdFilePath = Path.GetTempFileName();
                File.WriteAllText(_mockTbdFilePath, @"--- !tapi-tbd
tbd-version:     4
targets:         [ x86_64-macos, arm64-macos, arm64e-macos ]
install-name:    '/System/Library/Frameworks/TestFramework.framework/TestFramework'
swift-abi-version: 7
exports:
  - targets:         [ x86_64-macos, arm64-macos, arm64e-macos ]
    symbols:         [ '_$s4Test5ClassCMa', '_$s4Test5ClassCMn', '_$s4Test5ClassCfD' ]
    objc-classes:    [ TestClass1, TestClass2 ]
    objc-ivars:      [ TestClass1._property ]
");
            }

            [Fact]
            public static void AssureTbdExists()
            {
                Assert.False(String.IsNullOrEmpty(_tbdFilePath));
                Assert.True(File.Exists(_tbdFilePath));
            }

            [Fact]
            public static void ParseFile()
            {
                TbdFile tbdFile = _tbdParser.ParseFile(_tbdFilePath);
                Assert.NotNull(tbdFile);
            }

            [Fact]
            public static void ValidateFoundationTbdContents()
            {
                TbdFile tbdFile = _tbdParser.ParseFile(_tbdFilePath);

                // Basic properties
                Assert.Equal(4, tbdFile.Version); // Foundation.tbd uses version 4
                Assert.NotEmpty(tbdFile.Targets);

                // Validate targets — the exact target list in Foundation.tbd varies
                // across Xcode 26.3 builds (some ship bare arm64 alongside arm64e,
                // others ship arm64e only). Assert the universally present pairs.
                Assert.Contains("x86_64-macos", tbdFile.Targets);
                Assert.Contains("arm64e-macos", tbdFile.Targets);
                Assert.Contains("x86_64-maccatalyst", tbdFile.Targets);
                Assert.Contains("arm64e-maccatalyst", tbdFile.Targets);

                // Validate install name
                Assert.Contains("Foundation", tbdFile.InstallName);
                Assert.Equal("/System/Library/Frameworks/Foundation.framework/Versions/C/Foundation", tbdFile.InstallName);

                // Validate Swift ABI version
                Assert.Equal(7, tbdFile.SwiftAbiVersion);

                // Validate exports structure
                Assert.NotEmpty(tbdFile.Exports);

                // Validate that each export has targets and symbols
                Assert.All(tbdFile.Exports, export =>
                {
                    Assert.NotEmpty(export.Targets);
                    Assert.NotEmpty(export.Symbols);
                });
            }

            [Fact]
            public static void TestMockTbdFile()
            {
                TbdFile tbdFile = _tbdParser.ParseFile(_mockTbdFilePath);

                // Verify basic properties
                Assert.Equal(4, tbdFile.Version);
                Assert.Equal(3, tbdFile.Targets.Count);
                Assert.Equal("/System/Library/Frameworks/TestFramework.framework/TestFramework", tbdFile.InstallName);
                Assert.Equal(7, tbdFile.SwiftAbiVersion);

                // Verify exports
                Assert.Single(tbdFile.Exports);
                var export = tbdFile.Exports[0];

                // Verify targets
                Assert.Equal(3, export.Targets.Count);
                Assert.Contains("x86_64-macos", export.Targets);
                Assert.Contains("arm64-macos", export.Targets);
                Assert.Contains("arm64e-macos", export.Targets);

                // Verify symbols
                Assert.Equal(3, export.Symbols.Count);

                // Verify Swift symbols
                Assert.Equal(3, export.SwiftSymbols.ToList().Count);
                Assert.Contains(export.SwiftSymbols, s => s.Name == "_$s4Test5ClassCMa");
                Assert.Contains(export.SwiftSymbols, s => s.Name == "_$s4Test5ClassCMn");
                Assert.Contains(export.SwiftSymbols, s => s.Name == "_$s4Test5ClassCfD");

                // Verify Objective-C symbols
                Assert.Empty(export.ObjectiveCSymbols);

                // Verify other symbols
                Assert.Empty(export.OtherSymbols);

                // Verify Objective-C classes
                Assert.Equal(2, export.ObjcClasses.Count);
                Assert.Contains("TestClass1", export.ObjcClasses);
                Assert.Contains("TestClass2", export.ObjcClasses);

                // Verify Objective-C ivars
                Assert.Single(export.ObjcIvars);
                Assert.Contains("TestClass1._property", export.ObjcIvars);
            }

            [Fact]
            public static void TestUnknownMultiLineExportProperty_ObjcEhTypes()
            {
                // Mirrors the multi-line unknown TBD property shape where
                // `objc-eh-types: [ ... ]` spans multiple lines and is not in the parser's
                // recognized export-property switch. The parser must consume the continuation
                // lines so the tail (e.g. "STDSRuntimeException ]") is not re-fed as a
                // key-value pair, AND so a known property that comes after (objc-ivars here)
                // still parses correctly.
                string mockPath = Path.GetTempFileName();
                File.WriteAllText(mockPath, @"--- !tapi-tbd
tbd-version:     4
targets:         [ x86_64-ios-simulator, arm64-ios-simulator ]
install-name:    '/path/to/PaymentSdkPayments.framework/PaymentSdkPayments'
swift-abi-version: 7
exports:
  - targets:         [ x86_64-ios-simulator, arm64-ios-simulator ]
    symbols:         [ '_$s18PaymentSdkPayments5ClassCMa', '_$s18PaymentSdkPayments5ClassCMn' ]
    objc-classes:    [ PaymentSdkClass1, PaymentSdkClass2 ]
    objc-eh-types:   [ STDSAlreadyInitializedException, STDSException, STDSInvalidInputException,
                       STDSNotInitializedException, STDSRuntimeException ]
    objc-ivars:      [ PaymentSdkClass1._property ]
");

                try
                {
                    TbdFile tbdFile = _tbdParser.ParseFile(mockPath);

                    Assert.NotNull(tbdFile);
                    Assert.Single(tbdFile.Exports);
                    var export = tbdFile.Exports[0];

                    // Symbols and objc-classes (parsed before objc-eh-types) must be intact.
                    Assert.Equal(2, export.Symbols.Count);
                    Assert.Equal(2, export.ObjcClasses.Count);
                    Assert.Contains("PaymentSdkClass1", export.ObjcClasses);
                    Assert.Contains("PaymentSdkClass2", export.ObjcClasses);

                    // Critical: parsing did NOT bail on the multi-line unknown property.
                    // The objc-ivars line that follows the multi-line `objc-eh-types`
                    // array must still be picked up.
                    Assert.Single(export.ObjcIvars);
                    Assert.Contains("PaymentSdkClass1._property", export.ObjcIvars);
                }
                finally
                {
                    File.Delete(mockPath);
                }
            }

            [Fact]
            public static void TestUnknownSingleLineExportProperty_DoesNotConsumeNextLine()
            {
                // A single-line unknown export property must NOT cause the parser to
                // consume the next line — only multi-line arrays trigger consumption.
                string mockPath = Path.GetTempFileName();
                File.WriteAllText(mockPath, @"--- !tapi-tbd
tbd-version:     4
targets:         [ arm64-ios-simulator ]
install-name:    '/path/to/Test'
exports:
  - targets:         [ arm64-ios-simulator ]
    symbols:         [ '_$s4Test5HelloCMa' ]
    objc-eh-types:   [ STDSException ]
    objc-classes:    [ PaymentSdkClass1 ]
");

                try
                {
                    TbdFile tbdFile = _tbdParser.ParseFile(mockPath);
                    Assert.Single(tbdFile.Exports);
                    var export = tbdFile.Exports[0];
                    Assert.Single(export.Symbols);
                    Assert.Single(export.ObjcClasses);
                    Assert.Contains("PaymentSdkClass1", export.ObjcClasses);
                }
                finally
                {
                    File.Delete(mockPath);
                }
            }

            [Fact]
            public static void TestFileNotFound()
            {
                var nonExistentFile = "/path/to/nonexistent.tbd";
                Assert.Throws<FileNotFoundException>(() => _tbdParser.ParseFile(nonExistentFile));
            }

            [Fact]
            public static void TestInvalidTbdFormat()
            {
                // Create an invalid TBD file
                string invalidTbdPath = Path.GetTempFileName();
                File.WriteAllText(invalidTbdPath, "This is not a valid TBD file");

                try
                {
                    Assert.Throws<ParsingException>(() => _tbdParser.ParseFile(invalidTbdPath));
                }
                finally
                {
                    // Clean up temporary file
                    File.Delete(invalidTbdPath);
                }
            }

            [Fact]
            public static void TestMockJsonTbdFile()
            {
                string jsonTbdPath = Path.GetTempFileName();
                File.WriteAllText(jsonTbdPath, @"{
  ""tapi_tbd_version"": 5,
  ""main_library"": {
    ""target_info"": [
      { ""target"": ""arm64-ios"", ""min_deployment"": ""15"" },
      { ""target"": ""arm64-ios-simulator"", ""min_deployment"": ""15"" }
    ],
    ""install_names"": [
      { ""name"": ""@rpath/TestLib.framework/TestLib"" }
    ],
    ""swift_abi"": [
      { ""abi"": 7 }
    ],
    ""exported_symbols"": [
      {
        ""data"": {
          ""global"": [
            ""_$s7TestLib5ClassCMa"",
            ""_$s7TestLib5ClassCMn"",
            ""_globalFunc""
          ]
        }
      }
    ]
  }
}");

                try
                {
                    TbdFile tbdFile = _tbdParser.ParseFile(jsonTbdPath);

                    Assert.Equal(5, tbdFile.Version);
                    Assert.Equal(2, tbdFile.Targets.Count);
                    Assert.Contains("arm64-ios", tbdFile.Targets);
                    Assert.Contains("arm64-ios-simulator", tbdFile.Targets);
                    Assert.Equal("@rpath/TestLib.framework/TestLib", tbdFile.InstallName);
                    Assert.Equal(7, tbdFile.SwiftAbiVersion);

                    Assert.Single(tbdFile.Exports);
                    var export = tbdFile.Exports[0];
                    Assert.Equal(3, export.Symbols.Count);

                    // Swift symbols (start with _$s)
                    Assert.Equal(2, export.SwiftSymbols.Count());
                    Assert.Contains(export.SwiftSymbols, s => s.Name == "_$s7TestLib5ClassCMa");
                    Assert.Contains(export.SwiftSymbols, s => s.Name == "_$s7TestLib5ClassCMn");

                    // _globalFunc starts with _ (not _$) → classified as ObjectiveC
                    Assert.Single(export.ObjectiveCSymbols);
                }
                finally
                {
                    File.Delete(jsonTbdPath);
                }
            }

            [Fact]
            public static void TestJsonTbdWithObjcClasses()
            {
                string jsonTbdPath = Path.GetTempFileName();
                File.WriteAllText(jsonTbdPath, @"{
  ""tapi_tbd_version"": 5,
  ""main_library"": {
    ""target_info"": [
      { ""target"": ""arm64-ios"" }
    ],
    ""install_names"": [
      { ""name"": ""@rpath/ObjCLib.framework/ObjCLib"" }
    ],
    ""exported_symbols"": [
      {
        ""data"": {
          ""global"": [ ""_$s5ObjCLibClassCMa"" ],
          ""objc_class"": [ ""ObjCClass1"", ""ObjCClass2"" ],
          ""objc_ivar"": [ ""ObjCClass1._name"", ""ObjCClass1._value"" ]
        }
      }
    ]
  }
}");

                try
                {
                    TbdFile tbdFile = _tbdParser.ParseFile(jsonTbdPath);

                    Assert.Single(tbdFile.Exports);
                    var export = tbdFile.Exports[0];

                    Assert.Equal(2, export.ObjcClasses.Count);
                    Assert.Contains("ObjCClass1", export.ObjcClasses);
                    Assert.Contains("ObjCClass2", export.ObjcClasses);

                    Assert.Equal(2, export.ObjcIvars.Count);
                    Assert.Contains("ObjCClass1._name", export.ObjcIvars);
                    Assert.Contains("ObjCClass1._value", export.ObjcIvars);
                }
                finally
                {
                    File.Delete(jsonTbdPath);
                }
            }

            [Fact]
            public static void TestJsonTbdMissingOptionalFields()
            {
                string jsonTbdPath = Path.GetTempFileName();
                File.WriteAllText(jsonTbdPath, @"{
  ""tapi_tbd_version"": 5,
  ""main_library"": {
    ""exported_symbols"": [
      {
        ""data"": {
          ""global"": [ ""_$s4Test5FuncyyF"" ]
        }
      }
    ]
  }
}");

                try
                {
                    TbdFile tbdFile = _tbdParser.ParseFile(jsonTbdPath);

                    Assert.Equal(5, tbdFile.Version);
                    Assert.Empty(tbdFile.Targets);
                    Assert.Equal(string.Empty, tbdFile.InstallName);
                    Assert.Equal(0, tbdFile.SwiftAbiVersion);

                    // Symbols still parse
                    Assert.Single(tbdFile.Exports);
                    Assert.Single(tbdFile.Exports[0].Symbols);
                    Assert.Equal("_$s4Test5FuncyyF", tbdFile.Exports[0].Symbols[0].Name);
                }
                finally
                {
                    File.Delete(jsonTbdPath);
                }
            }

            [Fact]
            public static void TestJsonTbdWithTextSegment()
            {
                string jsonTbdPath = Path.GetTempFileName();
                File.WriteAllText(jsonTbdPath, @"{
  ""tapi_tbd_version"": 5,
  ""main_library"": {
    ""target_info"": [
      { ""target"": ""arm64-ios"" }
    ],
    ""install_names"": [
      { ""name"": ""@rpath/Mixed.framework/Mixed"" }
    ],
    ""exported_symbols"": [
      {
        ""data"": {
          ""global"": [ ""_$s5Mixed6StructVMa"", ""_$s5Mixed6StructVMn"" ]
        },
        ""text"": {
          ""global"": [ ""__ZN5swift39override_conformsToSwiftProtocolE"", ""__ZN5swift20class_getSuperclassE"" ]
        }
      }
    ]
  }
}");

                try
                {
                    TbdFile tbdFile = _tbdParser.ParseFile(jsonTbdPath);

                    Assert.Single(tbdFile.Exports);
                    var export = tbdFile.Exports[0];

                    // Both data and text symbols should be combined
                    Assert.Equal(4, export.Symbols.Count);

                    // 2 Swift symbols from data
                    Assert.Equal(2, export.SwiftSymbols.Count());

                    // 2 C++ symbols from text (start with __ → classified as ObjectiveC by prefix rule)
                    Assert.Equal(2, export.ObjectiveCSymbols.Count());
                    Assert.Contains(export.Symbols, s => s.Name == "__ZN5swift39override_conformsToSwiftProtocolE");
                    Assert.Contains(export.Symbols, s => s.Name == "__ZN5swift20class_getSuperclassE");
                }
                finally
                {
                    File.Delete(jsonTbdPath);
                }
            }

            [Fact]
            public static void TestJsonTbdParseThroughTbdParser()
            {
                string jsonTbdPath = Path.GetTempFileName();
                File.WriteAllText(jsonTbdPath, @"{
  ""tapi_tbd_version"": 5,
  ""main_library"": {
    ""target_info"": [
      { ""target"": ""arm64-macos"" }
    ],
    ""install_names"": [
      { ""name"": ""/usr/lib/libTest.dylib"" }
    ],
    ""swift_abi"": [
      { ""abi"": 7 }
    ],
    ""exported_symbols"": [
      {
        ""data"": {
          ""global"": [ ""_$s4Test5HelloCMa"" ]
        }
      }
    ]
  }
}");

                try
                {
                    // Parse through the top-level TbdParser (not direct parser)
                    TbdFile tbdFile = _tbdParser.ParseFile(jsonTbdPath);

                    Assert.NotNull(tbdFile);
                    Assert.Equal(5, tbdFile.Version);
                    Assert.Single(tbdFile.Targets);
                    Assert.Contains("arm64-macos", tbdFile.Targets);
                    Assert.Equal("/usr/lib/libTest.dylib", tbdFile.InstallName);
                    Assert.Equal(7, tbdFile.SwiftAbiVersion);
                    Assert.Single(tbdFile.Exports);
                    Assert.Single(tbdFile.Exports[0].Symbols);
                }
                finally
                {
                    File.Delete(jsonTbdPath);
                }
            }

            [Fact]
            public static void TestInvalidJsonTbdFormat()
            {
                string invalidJsonPath = Path.GetTempFileName();
                File.WriteAllText(invalidJsonPath, @"{""not_a_tbd"": true}");

                try
                {
                    // No tapi_tbd_version → CanParse returns false → "unsupported format"
                    Assert.Throws<ParsingException>(() => _tbdParser.ParseFile(invalidJsonPath));
                }
                finally
                {
                    File.Delete(invalidJsonPath);
                }
            }

            [Fact]
            public static void TestMalformedJsonTbdFormat()
            {
                string malformedPath = Path.GetTempFileName();
                File.WriteAllText(malformedPath, @"{broken json???");

                try
                {
                    // Invalid JSON → CanParse safely returns false → "unsupported format"
                    Assert.Throws<ParsingException>(() => _tbdParser.ParseFile(malformedPath));
                }
                finally
                {
                    File.Delete(malformedPath);
                }
            }

            public void Dispose()
            {
                // Clean up the mock TBD file
                if (File.Exists(_mockTbdFilePath))
                {
                    File.Delete(_mockTbdFilePath);
                }
            }
        }
    }
}
