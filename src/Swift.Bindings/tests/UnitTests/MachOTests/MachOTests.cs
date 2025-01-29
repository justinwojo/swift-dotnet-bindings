// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using BindingsGeneration.Demangling;
using Xamarin;
using Xunit;

namespace BindingsGeneration.Tests
{
    public class MachOTests : IClassFixture<MachOTests.TestFixture>
    {
        private readonly TestFixture _fixture;
        private static string _dylibPath;

        public MachOTests(TestFixture fixture)
        {
            _fixture = fixture;
        }

        public class TestFixture
        {
            static TestFixture()
            {
                InitializeResources();
            }

            private static void InitializeResources()
            {
                _dylibPath = "/System/Library/Frameworks/CoreFoundation.framework/Versions/A/Resources/BridgeSupport/CoreFoundation.dylib";
            }

            [Fact]
            public static void AssureDylibExists()
            {
                Assert.False(String.IsNullOrEmpty(_dylibPath));
                Assert.True(File.Exists(_dylibPath));
            }

            [Fact]
            public static void HasAbis()
            {
                var abis = MachO.GetArchitectures(_dylibPath);
                Assert.NotEmpty(abis);
            }

            [Fact]
            public static void IsMachOFile()
            {
                Assert.True(MachO.IsMachOFile(_dylibPath));
            }

            [Fact]
            public static void IsCorrectMinOS()
            {
                var macho = MachO.Read(_dylibPath).FirstOrDefault();
                Assert.NotNull(macho);
                var minOS = macho.load_commands.OfType<MinCommand>().FirstOrDefault();
                // this library is build without a minOS load command
                Assert.Null(minOS);
                var bv = macho.load_commands.OfType<BuildVersionCommand>().FirstOrDefault();
                Assert.NotNull(bv);
                Assert.Equal(MachO.Platform.MacOS, bv.Platform);
            }

            [Fact]
            public static void HasSymbols()
            {
                var abis = MachO.GetArchitectures(_dylibPath);
                var symbols = MachO.Read(_dylibPath).PublicSymbols(abis[0]);
                Assert.NotEmpty(symbols);
            }

            [Fact]
            public static void NoPrivate()
            {
                var abis = MachO.GetArchitectures(_dylibPath);
                var symbols = MachO.Read(_dylibPath).PublicSymbols(abis[0]);
                var any = symbols.Any(sy => sy.IsPrivate);
                Assert.False(any);
            }

            [Fact]
            public static void NoHighErrors()
            {
                var abis = MachO.GetArchitectures(_dylibPath);
                var demanglingResults = DemanglingResults.FromFile(_dylibPath, abis[0]);
                var highErrors = demanglingResults.Errors.Where(e => e.Severity == Demangling.ReductionErrorSeverity.High).ToArray();
                var highError = demanglingResults.Errors.FirstOrDefault(err => err.Severity == Demangling.ReductionErrorSeverity.High);
                Assert.True(highError is null, HighErrorToMessage(highError));
            }

            static string HighErrorToMessage(ReductionError err)
            {
                if (err is null)
                    return "no error";
                return $"Symbol {err.Symbol} -> {err.Message}";
            }
        }
    }
}
