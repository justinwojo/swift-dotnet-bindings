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

        /// <summary>
        /// A framework only some slices' SDKs ship (Xcode 27.2's CoreAI: device and macOS SDKs, no
        /// simulator) is slice-asymmetric; one every SDK ships, one none ships, or any module in a
        /// single-SDK run is not.
        /// </summary>
        [Fact]
        public void IsSliceAsymmetric_TrueOnlyForAModuleSomeSliceSdksLack()
        {
            var simulator = Path.Combine(_sdk, "simulator");
            var device = Path.Combine(_sdk, "device");
            WriteSdkModule(simulator, "Frameworks", "Shared", null);
            WriteSdkModule(device, "Frameworks", "Shared", null);
            WriteSdkModule(device, "Frameworks", "DeviceOnly", null);
            WriteSdkModule(device, "SubFrameworks", "DeviceOnlyDetail", null);
            Directory.CreateDirectory(Path.Combine(device, "usr", "lib", "swift", "DeviceOverlay.swiftmodule"));

            var names = new SdkPublicModuleNames(simulator, device);

            Assert.True(names.IsSliceAsymmetric("DeviceOnly"));
            Assert.True(names.IsSliceAsymmetric("DeviceOnlyDetail"));
            Assert.True(names.IsSliceAsymmetric("DeviceOverlay"));
            Assert.False(names.IsSliceAsymmetric("Shared"));
            Assert.False(names.IsSliceAsymmetric("ThirdPartyDependency"));
            Assert.False(new SdkPublicModuleNames(device).IsSliceAsymmetric("DeviceOnly"));
            Assert.False(new SdkPublicModuleNames(device, device).IsSliceAsymmetric("DeviceOnly"));
        }

        [Fact]
        public void ImportLines_GuardsOnlySliceAsymmetricModules()
        {
            var simulator = Path.Combine(_sdk, "simulator");
            var device = Path.Combine(_sdk, "device");
            WriteSdkModule(simulator, "Frameworks", "Shared", null);
            WriteSdkModule(device, "Frameworks", "Shared", null);
            WriteSdkModule(device, "Frameworks", "DeviceOnly", null);

            Assert.Equal(new[] { "import DeviceOnly" }, SdkPublicModuleNames.ImportLines("DeviceOnly"));
            using (SdkPublicModuleNames.Install(new SdkPublicModuleNames(simulator, device)))
            {
                Assert.Equal(new[] { "#if canImport(DeviceOnly)", "import DeviceOnly", "#endif" },
                    SdkPublicModuleNames.ImportLines("DeviceOnly"));
                Assert.Equal(new[] { "#if canImport(DeviceOnly)", "import DeviceOnly.Sub", "#endif" },
                    SdkPublicModuleNames.ImportLines("DeviceOnly.Sub"));
                Assert.Equal(new[] { "import Shared" }, SdkPublicModuleNames.ImportLines("Shared"));
                Assert.Equal(new[] { "import ThirdPartyDependency" }, SdkPublicModuleNames.ImportLines("ThirdPartyDependency"));
            }
        }

        /// <summary>
        /// Both legs of a guarded import compile: with the module on the search path (the slice whose
        /// SDK ships it) and without it (the slice whose SDK lacks it), where the plain import the
        /// guard replaces fails. Real swiftc, a module built here, so it holds on any installed Xcode.
        /// </summary>
        [SkippableFact]
        public void ImportLines_GuardedImportCompilesOnBothLegs()
        {
            Skip.IfNot(OperatingSystem.IsMacOS(), "swiftc is required");
            var runner = new SystemCommandRunner();
            var probe = runner.Run("xcrun", "--find swiftc");
            Skip.If(probe.ExitCode != 0, "xcrun swiftc is required");

            var work = Path.Combine(_sdk, "legs");
            var modules = Path.Combine(work, "modules");
            Directory.CreateDirectory(modules);
            var moduleSource = Path.Combine(work, "SBSliceOnly.swift");
            File.WriteAllText(moduleSource, "public struct SliceOnlyMarker { public init() {} }\n");
            var built = runner.Run("xcrun",
                $"swiftc -parse-as-library -emit-module -module-name SBSliceOnly -emit-module-path \"{Path.Combine(modules, "SBSliceOnly.swiftmodule")}\" \"{moduleSource}\"",
                timeoutMs: 120000);
            Assert.True(built.ExitCode == 0, built.StdErr);

            var simulator = Path.Combine(_sdk, "simulator");
            var device = Path.Combine(_sdk, "device");
            Directory.CreateDirectory(simulator);
            Directory.CreateDirectory(Path.Combine(device, "usr", "lib", "swift", "SBSliceOnly.swiftmodule"));
            IReadOnlyList<string> guarded;
            using (SdkPublicModuleNames.Install(new SdkPublicModuleNames(simulator, device)))
                guarded = SdkPublicModuleNames.ImportLines("SBSliceOnly");
            Assert.Equal("#if canImport(SBSliceOnly)", guarded[0]);

            string Typecheck(IEnumerable<string> imports, bool withModule)
            {
                var source = Path.Combine(work, $"wrapper-{Guid.NewGuid():N}.swift");
                File.WriteAllText(source, string.Join("\n", imports) + "\npublic func sbWrapperBody() -> Int32 { 1 }\n");
                var search = withModule ? $"-I \"{modules}\" " : "";
                var (exit, _, stderr) = runner.Run("xcrun", $"swiftc -typecheck -parse-as-library {search}\"{source}\"", timeoutMs: 120000);
                return exit == 0 ? "" : stderr;
            }

            Assert.Equal("", Typecheck(guarded, withModule: true));
            Assert.Equal("", Typecheck(guarded, withModule: false));
            Assert.Contains("SBSliceOnly", Typecheck(new[] { "import SBSliceOnly" }, withModule: false));
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
