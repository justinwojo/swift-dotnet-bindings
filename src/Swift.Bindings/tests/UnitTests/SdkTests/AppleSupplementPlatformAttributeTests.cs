// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Durable gate for the hand-authored Apple-supplement facades
    /// (<c>src/Swift.Bindings.Apple/Sources/**</c>).
    ///
    /// Generated Apple types carry per-type <c>[SupportedOSPlatform]</c> /
    /// <c>[UnsupportedOSPlatform]</c> attributes so the .NET platform-compatibility
    /// analyzer (CA1416) warns consumers who call below a type's real OS floor. The
    /// hand-authored facades do NOT go through the generator's availability pass, so
    /// they historically fell in the gap: they gate availability at *runtime*
    /// (throwing <c>PlatformNotSupportedException</c> / branching on
    /// <c>OperatingSystem.Is*</c>) but exposed no *compile-time* metadata, leaving
    /// consumers with no build-time warning before the runtime throw. See
    /// <c>src/docs/Future/supplement-facade-supportedosplatform.md</c>.
    ///
    /// These tests assert the convention that closes the gap: any facade file that
    /// gates availability at runtime must also carry a compile-time platform
    /// attribute. They are source-based (not reflection over a built DLL) because the
    /// supplement is not part of the unit-test solution and is not built by
    /// <c>nuke test</c>, so its assembly is not reliably present when these run.
    ///
    /// Scope: this is a lightweight *presence* gate (plus the high-value
    /// Catalyst-inheritance rule). It deliberately does not verify which declaration an
    /// attribute sits on — over-restriction (e.g. putting a Catalyst exclusion on the
    /// <c>Text</c> type instead of just <c>Text.Create</c>) is caught by the supplement's
    /// own CA1416 build, which fails because the module-init factory registration
    /// references the type on Catalyst. Checking declaration scope from source text would
    /// need a real C# parser; the compile gate already covers that failure mode.
    /// </summary>
    public class AppleSupplementPlatformAttributeTests
    {
        private static readonly string SourcesDir = Path.Combine(
            FindRepoRoot(), "src", "Swift.Bindings.Apple", "Sources");

        // A facade gates availability at runtime if it throws PlatformNotSupportedException
        // or branches on OperatingSystem.Is* — both signal a platform floor / exclusion that
        // the analyzer cannot see without a matching attribute.
        private static bool HasRuntimePlatformGuard(string content) =>
            content.Contains("throw new PlatformNotSupportedException")
            || Regex.IsMatch(content, @"OperatingSystem\.Is\w+");

        // The compile-time counterpart the analyzer reads. Either direction counts:
        // a floor (SupportedOSPlatform) or an exclusion (UnsupportedOSPlatform).
        private static bool HasPlatformAttribute(string content) =>
            content.Contains("[SupportedOSPlatform(")
            || content.Contains("[UnsupportedOSPlatform(");

        // A facade gates against Mac Catalyst if it branches on OperatingSystem.IsMacCatalyst()
        // (either "!IsMacCatalyst() => proceed" or "IsMacCatalyst() => throw").
        private static bool GatesAgainstMacCatalyst(string content) =>
            content.Contains("IsMacCatalyst(");

        // Mac Catalyst is the one platform the .NET analyzer treats as INHERITING iOS
        // support: a bare [SupportedOSPlatform("ios…")] does NOT warn a Catalyst consumer.
        // So a facade that throws on Catalyst at runtime needs this explicit exclusion or
        // its CA1416 coverage is silently blind on the macabi TFM.
        private static bool HasMacCatalystExclusion(string content) =>
            Regex.IsMatch(content, @"\[UnsupportedOSPlatform\(\s*""maccatalyst""");

        public static IEnumerable<object[]> GuardedFacadeFiles()
        {
            foreach (var path in Directory.EnumerateFiles(SourcesDir, "*.cs", SearchOption.AllDirectories))
            {
                if (HasRuntimePlatformGuard(File.ReadAllText(path)))
                    yield return new object[] { Path.GetRelativePath(SourcesDir, path) };
            }
        }

        [Theory]
        [MemberData(nameof(GuardedFacadeFiles))]
        public void GuardedFacade_CarriesPlatformAttribute(string relativePath)
        {
            // Every hand-authored facade that gates availability at runtime must also
            // expose compile-time platform metadata, so CA1416 warns a consumer before
            // they reach the runtime throw. Without this the supplement silently
            // regresses to "runtime-guarded but analyzer-blind" as new facades land.
            var content = File.ReadAllText(Path.Combine(SourcesDir, relativePath));
            Assert.True(
                HasPlatformAttribute(content),
                $"Facade '{relativePath}' gates availability at runtime "
                + "(PlatformNotSupportedException / OperatingSystem.Is*) but carries no "
                + "[SupportedOSPlatform] / [UnsupportedOSPlatform] attribute. Add the "
                + "compile-time attribute matching its runtime floor so the CA1416 "
                + "analyzer can warn consumers before the runtime throw.");
        }

        [Theory]
        [MemberData(nameof(GuardedFacadeFiles))]
        public void MacCatalystGuardedFacade_CarriesExplicitCatalystExclusion(string relativePath)
        {
            // The Catalyst-inheritance trap: the analyzer treats Mac Catalyst as inheriting
            // iOS support, so a facade that throws PlatformNotSupportedException on Catalyst
            // gets NO CA1416 warning from [SupportedOSPlatform("ios…")] alone. It must carry
            // an explicit [UnsupportedOSPlatform("maccatalyst")] (on the type or the guarded
            // member). Without this rule the general attribute check above would pass a
            // Catalyst-blind facade — exactly the gap that shipped LiveActivity iOS-only.
            var content = File.ReadAllText(Path.Combine(SourcesDir, relativePath));
            if (!GatesAgainstMacCatalyst(content)) return; // not a Catalyst-gating facade

            Assert.True(
                HasMacCatalystExclusion(content),
                $"Facade '{relativePath}' gates against Mac Catalyst at runtime "
                + "(OperatingSystem.IsMacCatalyst()) but carries no "
                + "[UnsupportedOSPlatform(\"maccatalyst\")]. The analyzer treats Catalyst as "
                + "inheriting iOS support, so a bare [SupportedOSPlatform(\"ios…\")] would NOT "
                + "warn Catalyst consumers. Add the explicit Catalyst exclusion.");
        }

        [Fact]
        public void Gate_DiscoversTheKnownGuardedFacades()
        {
            // Guards the gate itself: if the Sources layout moves and discovery silently
            // returns nothing, the Theory above passes vacuously. Pin the two facades
            // that are known to gate at runtime today so a zero-file or moved-directory
            // regression fails loudly instead.
            var guarded = GuardedFacadeFiles()
                .Select(row => ((string)row[0]).Replace('\\', '/'))
                .ToList();

            Assert.Contains("ActivityKit/LiveActivity.cs", guarded);
            Assert.Contains("SwiftUI/Text.cs", guarded);
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                var gitPath = Path.Combine(dir, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Cannot find repo root.");
        }
    }
}
