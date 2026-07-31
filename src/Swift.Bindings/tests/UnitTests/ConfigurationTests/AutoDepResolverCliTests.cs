// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// End-to-end CLI tests for the <c>--resolve-auto-deps</c> verb (D6). The SDK's
    /// <c>_ResolveSwiftAutoDetectedDependencies</c> target captures this verb's stdout via
    /// <c>ConsoleToMSBuild</c> and matches every line with <c>.StartsWith("PROJREF|")</c> /
    /// <c>.StartsWith("WARN|")</c>, so stdout is a FROZEN grammar: it must carry those two line
    /// shapes and nothing else. Any diagnostic must go to stderr instead (a stray stdout line is
    /// silently dropped by the SDK, hiding the real error). These tests pin that contract at the
    /// default verbosity the SDK actually invokes with.
    /// </summary>
    [Collection("ConsoleCapture")]
    public class AutoDepResolverCliTests
    {
        [Fact]
        public void ResolveAutoDeps_ResolvableAndUnresolvableSpecs_StdoutIsOnlyFrozenGrammar()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "autodep-cli-" + Guid.NewGuid().ToString("N"));
            var siblingDir = Path.Combine(tempRoot, "sib");
            Directory.CreateDirectory(siblingDir);
            try
            {
                // A sibling "<packageId>.csproj" next to the xcframework makes the first probe hit,
                // producing a PROJREF| line. The second record points at a directory with no csproj,
                // producing a WARN| line. Together they exercise both grammar shapes in one run.
                File.WriteAllText(Path.Combine(siblingDir, "Alpha.csproj"), "<Project />");
                var resolvableXcfw = Path.Combine(siblingDir, "Alpha.xcframework");
                var unresolvableXcfw = Path.Combine(tempRoot, "gone", "Beta.xcframework");
                var spec = $"AlphaMod|Alpha|1.0.0|{resolvableXcfw};BetaMod|Beta|2.0.0|{unresolvableXcfw}";

                var (stdout, stderr, exitCode) = RunResolveAutoDeps(spec, explicitDeps: "");

                Assert.Equal(0, exitCode);

                var stdoutLines = SplitNonEmptyLines(stdout);
                Assert.NotEmpty(stdoutLines);
                Assert.All(stdoutLines, line =>
                    Assert.True(
                        line.StartsWith("PROJREF|", StringComparison.Ordinal) ||
                        line.StartsWith("WARN|", StringComparison.Ordinal),
                        $"stdout line violated the frozen PROJREF|/WARN| grammar: '{line}'"));

                // Both shapes must actually appear — proves the assertion above isn't vacuously true.
                Assert.Contains(stdoutLines, l => l.StartsWith("PROJREF|", StringComparison.Ordinal) && l.EndsWith("Alpha.csproj", StringComparison.Ordinal));
                Assert.Contains(stdoutLines, l => l.StartsWith("WARN|BetaMod|Beta|2.0.0|", StringComparison.Ordinal));

                // The grammar lives on stdout only — stderr must never carry a PROJREF|/WARN| line.
                var stderrLines = SplitNonEmptyLines(stderr);
                Assert.DoesNotContain(stderrLines, l =>
                    l.StartsWith("PROJREF|", StringComparison.Ordinal) ||
                    l.StartsWith("WARN|", StringComparison.Ordinal));
            }
            finally
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        [Fact]
        public void ResolveAutoDeps_DifferentlyNamedSiblingBindingProject_ResolvesAgainstTheRealFilesystem()
        {
            // The verb's REAL probes (Directory.GetFiles + File.ReadAllText) must find a binding
            // project whose file name has nothing to do with the synthesized `{Module}.Swift.{Platform}`
            // package id — the swift-dotnet-packages shape, where `FBAEMKit.xcframework` sits next to
            // `SwiftBindings.Facebook.AEM.csproj`. Unit tests inject those probes; this pins the wiring.
            var tempRoot = Path.Combine(Path.GetTempPath(), "autodep-cli-" + Guid.NewGuid().ToString("N"));
            var depDir = Path.Combine(tempRoot, "FBAEMKit");
            Directory.CreateDirectory(depDir);
            try
            {
                var bindingProject = Path.Combine(depDir, "SwiftBindings.Facebook.AEM.csproj");
                File.WriteAllText(bindingProject, "<Project Sdk=\"SwiftBindings.Sdk/0.18.0\"></Project>");
                // A non-binding project in the same directory must not create ambiguity.
                File.WriteAllText(Path.Combine(depDir, "Unrelated.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

                var xcfw = Path.Combine(depDir, "FBAEMKit.xcframework");
                var spec = $"FBAEMKit|FBAEMKit.Swift.iOS|0.0.0|{xcfw}";

                var (stdout, _, exitCode) = RunResolveAutoDeps(spec, explicitDeps: "");

                Assert.Equal(0, exitCode);
                var lines = SplitNonEmptyLines(stdout);
                var projRef = Assert.Single(lines);
                Assert.StartsWith("PROJREF|", projRef, StringComparison.Ordinal);
                Assert.EndsWith("SwiftBindings.Facebook.AEM.csproj", projRef, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        [Fact]
        public void ResolveAutoDeps_ConsumerProjectBesideTheDependencyXcframework_WarnsInsteadOfSelfReferencing()
        {
            // --consumer-project carries $(MSBuildProjectFullPath) from the SDK Exec. When the
            // project being built is the only binding csproj in a dependency xcframework's
            // directory, the probe must skip it rather than emit PROJREF| for the project itself
            // (MSBuild would then fail with a circular ProjectReference). Pins the CLI wiring of
            // the option, not just the resolver logic.
            var tempRoot = Path.Combine(Path.GetTempPath(), "autodep-cli-" + Guid.NewGuid().ToString("N"));
            var depDir = Path.Combine(tempRoot, "Facebook");
            Directory.CreateDirectory(depDir);
            try
            {
                var self = Path.Combine(depDir, "SwiftBindings.Facebook.Core.csproj");
                File.WriteAllText(self, "<Project Sdk=\"SwiftBindings.Sdk/0.18.0\"></Project>");

                var xcfw = Path.Combine(depDir, "FBSDKCoreKit_Basics.xcframework");
                var spec = $"FBSDKCoreKit_Basics|FBSDKCoreKit_Basics.Swift.iOS|0.0.0|{xcfw}";

                var (withSelf, _, selfExit) = RunResolveAutoDeps(spec, explicitDeps: "", consumerProject: self);
                Assert.Equal(0, selfExit);
                var selfLine = Assert.Single(SplitNonEmptyLines(withSelf));
                Assert.StartsWith("WARN|FBSDKCoreKit_Basics|", selfLine, StringComparison.Ordinal);

                // Control: the same layout WITHOUT the exclusion resolves to that very project —
                // proving the assertion above is the exclusion at work, not an unrelated miss.
                var (withoutSelf, _, exit) = RunResolveAutoDeps(spec, explicitDeps: "");
                Assert.Equal(0, exit);
                var line = Assert.Single(SplitNonEmptyLines(withoutSelf));
                Assert.StartsWith("PROJREF|", line, StringComparison.Ordinal);
                Assert.EndsWith("SwiftBindings.Facebook.Core.csproj", line, StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }

        [Fact]
        public void ResolveAutoDeps_EmptySpec_ProducesNoStdout()
        {
            var (stdout, _, exitCode) = RunResolveAutoDeps(autoDepSpec: "", explicitDeps: "");
            Assert.Equal(0, exitCode);
            Assert.Empty(SplitNonEmptyLines(stdout));
        }

        private static (string stdout, string stderr, int exitCode) RunResolveAutoDeps(
            string autoDepSpec, string explicitDeps, string? consumerProject = null)
        {
            using var capture = ConsoleCapture.Begin();
            var args = new List<string>
            {
                "--resolve-auto-deps",
                "--auto-dep-spec", autoDepSpec,
                "--explicit-deps", explicitDeps,
            };
            if (consumerProject is not null)
            {
                args.Add("--consumer-project");
                args.Add(consumerProject);
            }

            // No --verbose: runs at the default Information verbosity the SDK Exec uses.
            var exitCode = BindingsGenerator.Main(args.ToArray());
            return (capture.Out, capture.Error, exitCode);
        }

        private static string[] SplitNonEmptyLines(string text) =>
            text.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToArray();
    }
}
