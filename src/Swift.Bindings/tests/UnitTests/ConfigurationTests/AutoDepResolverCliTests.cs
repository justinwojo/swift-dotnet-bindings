// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
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
        public void ResolveAutoDeps_EmptySpec_ProducesNoStdout()
        {
            var (stdout, _, exitCode) = RunResolveAutoDeps(autoDepSpec: "", explicitDeps: "");
            Assert.Equal(0, exitCode);
            Assert.Empty(SplitNonEmptyLines(stdout));
        }

        private static (string stdout, string stderr, int exitCode) RunResolveAutoDeps(string autoDepSpec, string explicitDeps)
        {
            var stdoutWriter = new StringWriter();
            var stderrWriter = new StringWriter();
            var originalOut = Console.Out;
            var originalError = Console.Error;
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);
            try
            {
                // No --verbose: runs at the default Information verbosity the SDK Exec uses.
                var exitCode = BindingsGenerator.Main(new[]
                {
                    "--resolve-auto-deps",
                    "--auto-dep-spec", autoDepSpec,
                    "--explicit-deps", explicitDeps,
                });
                return (stdoutWriter.ToString(), stderrWriter.ToString(), exitCode);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }

        private static string[] SplitNonEmptyLines(string text) =>
            text.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToArray();
    }
}
