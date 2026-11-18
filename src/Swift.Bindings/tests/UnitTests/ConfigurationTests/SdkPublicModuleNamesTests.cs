// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// An SDK module that declares <c>-public-module-name</c> must be imported through its public
    /// module: swiftc refuses the direct import when the module is loaded from the SDK.
    /// </summary>
    public sealed class SdkPublicModuleNamesTests : IDisposable
    {
        private readonly string _sdk = Path.Combine(Path.GetTempPath(), "sb-fake-sdk-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_sdk))
                Directory.Delete(_sdk, recursive: true);
        }

        /// <summary>Writes a module's interface the way the Xcode 27.2 SDKs lay one out.</summary>
        internal static void WriteSdkModule(string sdkRoot, string frameworksDir, string module, string? publicModule)
        {
            var dir = Path.Combine(sdkRoot, "System", "Library", frameworksDir, $"{module}.framework", "Modules", $"{module}.swiftmodule");
            Directory.CreateDirectory(dir);
            var ignorable = publicModule is null
                ? "-strict-memory-safety -interface-compiler-version 6.4.2"
                : $"-strict-memory-safety -public-module-name {publicModule} -formal-cxx-interoperability-mode=off -interface-compiler-version 6.4.2";
            File.WriteAllText(Path.Combine(dir, "arm64-apple-ios-simulator.swiftinterface"),
                "// swift-interface-format-version: 1.0\n" +
                "// swift-compiler-version: Apple Swift version 6.4.2\n" +
                $"// swift-module-flags: -target arm64-apple-ios27.2-simulator -enable-library-evolution -module-name {module}\n" +
                $"// swift-module-flags-ignorable: {ignorable}\n" +
                "import Swift\n" +
                "public struct Marker {}\n");
        }

        [Theory]
        [InlineData("// swift-module-flags-ignorable: -strict-memory-safety -public-module-name RealityKit -interface-compiler-version 6.4.2", "RealityKit")]
        [InlineData("// swift-module-flags: -module-name AppIntentsTypeSupport -public-module-name AppIntents", "AppIntents")]
        [InlineData("// swift-module-flags-ignorable: -public-module-name=SwiftUI", "SwiftUI")]
        [InlineData("// swift-module-flags-ignorable: -strict-memory-safety -interface-compiler-version 6.4.2", null)]
        [InlineData("// swift-compiler-version: -public-module-name NotAFlagsLine", null)]
        public void ParsePublicModuleName_ReadsOnlyTheModuleFlagLines(string line, string? expected)
        {
            Assert.Equal(expected, SdkPublicModuleNames.ParsePublicModuleName(new[] { line }));
        }

        [Fact]
        public void TryGetPublicModuleName_FindsSubFrameworksAndFrameworks()
        {
            WriteSdkModule(_sdk, "SubFrameworks", "Detail", "Umbrella");
            WriteSdkModule(_sdk, "Frameworks", "TypeSupport", "Intents");
            WriteSdkModule(_sdk, "Frameworks", "Umbrella", null);

            var names = new SdkPublicModuleNames(_sdk);

            Assert.Equal("Umbrella", names.TryGetPublicModuleName("Detail"));
            Assert.Equal("Intents", names.TryGetPublicModuleName("TypeSupport"));
            Assert.Null(names.TryGetPublicModuleName("Umbrella"));
            Assert.Null(names.TryGetPublicModuleName("NotInTheSdk"));
        }

        /// <summary>
        /// The wrapper source is compiled for every slice, and a device SDK can hold an
        /// implementation-detail module its simulator SDK lacks; the lookup must find it there.
        /// </summary>
        [Fact]
        public void TryGetPublicModuleName_ConsultsEverySliceSdk()
        {
            var simulator = Path.Combine(_sdk, "simulator");
            var device = Path.Combine(_sdk, "device");
            WriteSdkModule(simulator, "Frameworks", "Shared", null);
            WriteSdkModule(device, "Frameworks", "Shared", null);
            WriteSdkModule(simulator, "Frameworks", "FlaggedOnDevice", null);
            WriteSdkModule(device, "Frameworks", "FlaggedOnDevice", "Umbrella");
            WriteSdkModule(device, "SubFrameworks", "DeviceOnlyDetail", "Umbrella");

            var names = new SdkPublicModuleNames(simulator, device);

            Assert.Equal("Umbrella", names.TryGetPublicModuleName("DeviceOnlyDetail"));
            Assert.Null(names.TryGetPublicModuleName("Shared"));
            Assert.Equal("Umbrella", names.TryGetPublicModuleName("FlaggedOnDevice"));
        }

        [Fact]
        public void TryGetPublicModuleName_SelfNamedFlagIsNotARemap()
        {
            WriteSdkModule(_sdk, "Frameworks", "Selfish", "Selfish");
            Assert.Null(new SdkPublicModuleNames(_sdk).TryGetPublicModuleName("Selfish"));
        }

        [Fact]
        public void TryGetPublicModuleName_NoSdkAnswersNull()
        {
            Assert.Null(new SdkPublicModuleNames((string?)null).TryGetPublicModuleName("Detail"));
        }

        [Fact]
        public void MapModuleToCompileImport_PrefersTheInterfaceDeclaredPublicModule()
        {
            WriteSdkModule(_sdk, "SubFrameworks", "Detail", "Umbrella");

            Assert.Equal("Detail", AppleFrameworkRegistry.MapModuleToCompileImport("Detail"));
            using (SdkPublicModuleNames.Install(new SdkPublicModuleNames(_sdk)))
            {
                Assert.Equal("Umbrella", AppleFrameworkRegistry.MapModuleToCompileImport("Detail"));
                // A module the SDK does not hold keeps the curated fallback.
                Assert.Equal("RealityKit", AppleFrameworkRegistry.MapModuleToCompileImport("RealityFoundation"));
                Assert.Equal("UIKit", AppleFrameworkRegistry.MapModuleToCompileImport("UIKit"));
            }
            Assert.Equal("Detail", AppleFrameworkRegistry.MapModuleToCompileImport("Detail"));
        }
    }
}
