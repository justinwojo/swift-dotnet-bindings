// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Finding 58 (toolchain identity): pins the supported-toolchain envelope so the three places it is
    /// declared can never silently drift apart — the C# source of truth
    /// (<see cref="SupportedToolchain"/>), the committed human-facing matrix
    /// (<c>build/supported-toolchain.json</c>), the README "Requires" floor line, and the swift-syntax
    /// pin in <c>tools/SwiftInterfaceParser/Package.{swift,resolved}</c>. Also exercises
    /// <see cref="SupportedToolchain.AssertSupported"/>'s tri-state classification (in-envelope info /
    /// out-of-envelope degradation / unobservable warn-only) against the ambient
    /// <see cref="InputResolutionReport"/>.
    /// </summary>
    public class SupportedToolchainMatrixTests
    {
        // ---- JSON ⇄ constants parity (the two cannot drift) ----

        [Fact]
        public void MatrixJson_EveryField_EqualsTheCorrespondingConstant()
        {
            var json = LoadMatrixJson();

            Assert.Equal(SupportedToolchain.MinXcodeMajor, (int)json["minXcodeMajor"]!);
            Assert.Equal(SupportedToolchain.MaxXcodeMajor, (int)json["maxXcodeMajor"]!);
            Assert.Equal(SupportedToolchain.MinDotnetSdk, (string)json["minDotnetSdk"]!);
            Assert.Equal(SupportedToolchain.ExpectedAbiFormatVersion, (int)json["expectedAbiFormatVersion"]!);
            Assert.Equal(SupportedToolchain.PinnedSwiftSyntaxVersion, (string)json["pinnedSwiftSyntaxVersion"]!);
            Assert.Equal(SupportedToolchain.PinnedSwiftSyntaxRevision, (string)json["pinnedSwiftSyntaxRevision"]!);
        }

        [Fact]
        public void MatrixJson_HasNoDataFieldWithoutABackingConstant()
        {
            // Reverse direction: every non-doc JSON field is one of the asserted-above keys, so a field
            // added to the matrix without a constant (or vice versa) turns this red. Underscore-prefixed
            // keys (e.g. "_doc") are commentary and exempt.
            var json = LoadMatrixJson();
            var known = new[]
            {
                "minXcodeMajor", "maxXcodeMajor", "minDotnetSdk",
                "expectedAbiFormatVersion", "pinnedSwiftSyntaxVersion", "pinnedSwiftSyntaxRevision",
            };
            var unexpected = json.Properties()
                .Select(p => p.Name)
                .Where(name => !name.StartsWith("_", StringComparison.Ordinal) && !known.Contains(name))
                .ToList();
            Assert.True(unexpected.Count == 0, $"unexpected matrix field(s) with no backing constant: {string.Join(", ", unexpected)}");
        }

        // ---- single owner of the ABI format version (Finding 58 forward) ----

        [Fact]
        public void ExpectedAbiFormatVersion_HasOneOwner_ParserForwardsToSupportedToolchain()
        {
            Assert.Equal(SupportedToolchain.ExpectedAbiFormatVersion, SwiftABIParser.ExpectedAbiFormatVersion);
        }

        // ---- README floor line is pinned to the constants ----

        [Fact]
        public void Readme_RequiresLine_StatesTheSupportedFloor()
        {
            var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
            Assert.Contains($"Xcode {SupportedToolchain.MinXcodeMajor}", readme);
            var dotnetMajor = SupportedToolchain.MinDotnetSdk.Split('.')[0];
            Assert.Contains($".NET {dotnetMajor}", readme);
        }

        // ---- swift-syntax pin matches the build manifest (amendment E) ----

        [Fact]
        public void PinnedSwiftSyntax_MatchesPackageManifest()
        {
            var packageSwift = File.ReadAllText(
                Path.Combine(RepoRoot(), "tools", "SwiftInterfaceParser", "Package.swift"));
            // Package.swift pins the version tag exactly: let swiftSyntaxVersion: Version = "601.0.1"
            Assert.Contains($"\"{SupportedToolchain.PinnedSwiftSyntaxVersion}\"", packageSwift);

            var packageResolved = File.ReadAllText(
                Path.Combine(RepoRoot(), "tools", "SwiftInterfaceParser", "Package.resolved"));
            Assert.Contains(SupportedToolchain.PinnedSwiftSyntaxVersion, packageResolved);
            Assert.Contains(SupportedToolchain.PinnedSwiftSyntaxRevision, packageResolved);
        }

        // ---- ParseXcodeMajor ----

        [Theory]
        [InlineData("Xcode 26.3\nBuild version 17C5051f", 26)]
        [InlineData("Xcode 26\nBuild version 17A123", 26)]
        [InlineData("Xcode 27.0\nBuild version 18A1", 27)]
        [InlineData("Xcode 9.4.1\nBuild version 9F2000", 9)]
        public void ParseXcodeMajor_ParsesTheLeadingMajor(string banner, int expected)
        {
            Assert.Equal(expected, SupportedToolchain.ParseXcodeMajor(banner));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not an xcode banner")]
        [InlineData(null)]
        public void ParseXcodeMajor_ReturnsNull_OnUnrecognizedBanner(string? banner)
        {
            Assert.Null(SupportedToolchain.ParseXcodeMajor(banner));
        }

        // ---- AssertSupported tri-state ----

        [Fact]
        public void AssertSupported_InEnvelope_RecordsInfo_NoWarningNoDegradation()
        {
            InputResolutionReport.Reset();
            var logger = new CapturingLogger();
            var runner = new MockCommandRunner();
            runner.SetResponse("xcodebuild -version", 0, $"Xcode {SupportedToolchain.MinXcodeMajor}.3\nBuild version 17C", "");

            SupportedToolchain.AssertSupported(runner, logger);

            Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND055"));
            Assert.Contains(
                InputResolutionReport.Decisions,
                d => d.Category == InputResolutionCategory.Toolchain && d.Severity == InputResolutionSeverity.Info);
            Assert.DoesNotContain(
                InputResolutionReport.Decisions,
                d => d.Category == InputResolutionCategory.Toolchain && d.Severity == InputResolutionSeverity.Degradation);
        }

        [Fact]
        public void AssertSupported_BelowFloor_WarnsSwiftbind055AndRecordsToolchainDegradation()
        {
            InputResolutionReport.Reset();
            var logger = new CapturingLogger();
            var runner = new MockCommandRunner();
            runner.SetResponse("xcodebuild -version", 0, $"Xcode {SupportedToolchain.MinXcodeMajor - 1}.0\nBuild version 16A", "");

            SupportedToolchain.AssertSupported(runner, logger);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND055"));
            Assert.Contains(
                InputResolutionReport.Decisions,
                d => d.Category == InputResolutionCategory.Toolchain
                     && d.Severity == InputResolutionSeverity.Degradation
                     && d.Detail.Contains("older"));
        }

        [Fact]
        public void AssertSupported_AboveTestedCeiling_WarnsSwiftbind055AndRecordsToolchainDegradation()
        {
            InputResolutionReport.Reset();
            var logger = new CapturingLogger();
            var runner = new MockCommandRunner();
            runner.SetResponse("xcodebuild -version", 0, $"Xcode {SupportedToolchain.MaxXcodeMajor + 1}.0\nBuild version 18A", "");

            SupportedToolchain.AssertSupported(runner, logger);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND055"));
            Assert.Contains(
                InputResolutionReport.Decisions,
                d => d.Category == InputResolutionCategory.Toolchain
                     && d.Severity == InputResolutionSeverity.Degradation
                     && d.Detail.Contains("newer"));
        }

        [Fact]
        public void AssertSupported_Unobservable_WarnsButRecordsNoDegradation()
        {
            // A host where `xcodebuild -version` fails (non-zero exit) must NOT fail-close --strict-inputs:
            // "could not verify" is not "verified out of range".
            InputResolutionReport.Reset();
            var logger = new CapturingLogger();
            var runner = new MockCommandRunner();
            runner.SetResponse("xcodebuild -version", 1, "", "xcode-select: error: tool 'xcodebuild' requires Xcode");

            SupportedToolchain.AssertSupported(runner, logger);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SWIFTBIND055"));
            Assert.DoesNotContain(
                InputResolutionReport.Decisions,
                d => d.Category == InputResolutionCategory.Toolchain && d.Severity == InputResolutionSeverity.Degradation);
        }

        // ---- helpers ----

        private static JObject LoadMatrixJson() =>
            JObject.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "build", "supported-toolchain.json")));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not locate repo root (SwiftBindings.sln) from " + AppContext.BaseDirectory);
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
