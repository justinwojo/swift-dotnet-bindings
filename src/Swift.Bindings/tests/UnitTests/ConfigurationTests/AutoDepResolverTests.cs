// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Unit tests for <see cref="AutoDepResolver"/> — the typed replacement for the SDK's
    /// former inline POSIX-sh cross-module dependency resolver (architecture-review-2026-06
    /// Finding 1). These pin the behavior the migrated <c>_ResolveSwiftAutoDetectedDependencies</c>
    /// target depends on: the FROZEN PROJREF|/WARN| line grammar, percent-decode order, the
    /// candidate-csproj probe order, and dedup against explicitly-declared dependencies.
    ///
    /// The probe (<c>fileExists</c>) and absolute-path normalizer (<c>toAbsolutePath</c>) are
    /// injected, so every case is a pure, deterministic function of its inputs — no real
    /// filesystem, no ordering flakiness.
    /// </summary>
    public class AutoDepResolverTests
    {
        // Identity normalizer: PROJREF| lines echo the matched candidate path verbatim,
        // so assertions can name the exact expected path.
        private static string Identity(string p) => p;

        // Probe that "finds" exactly the supplied set of paths.
        private static Func<string, bool> Exists(params string[] present)
        {
            var set = new HashSet<string>(present, StringComparer.Ordinal);
            return set.Contains;
        }

        private static List<string> Resolve(
            string? spec, string? explicitDeps, Func<string, bool> fileExists, Func<string, string>? toAbs = null)
            => AutoDepResolver.Resolve(spec, explicitDeps, fileExists, toAbs ?? Identity).ToList();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EmptySpec_ProducesNoLines(string? spec)
        {
            var result = Resolve(spec, explicitDeps: "", Exists());
            Assert.Empty(result);
        }

        [Fact]
        public void ResolvableSibling_EmitsProjRefWithAbsolutePath()
        {
            // grandparent = /root, parent = /root/sheet
            // candidate 1 (parent/PKG.csproj) hits.
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var hit = "/root/sheet/Core.Swift.iOS.csproj";

            var result = Resolve(spec, "", Exists(hit));

            Assert.Equal(new[] { "PROJREF|" + hit }, result);
        }

        [Theory]
        // candidate 1: <parent>/<pkg>.csproj
        [InlineData("/root/sheet/Core.Swift.iOS.csproj")]
        // candidate 2: <grandparent>/<pkg>/<pkg>.csproj
        [InlineData("/root/Core.Swift.iOS/Core.Swift.iOS.csproj")]
        // candidate 3: <grandparent>/<module>/<pkg>.csproj
        [InlineData("/root/Core/Core.Swift.iOS.csproj")]
        // candidate 4: <grandparent>/<pkg>.csproj
        [InlineData("/root/Core.Swift.iOS.csproj")]
        public void EachCandidatePath_IsProbed(string existingPath)
        {
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";

            var result = Resolve(spec, "", Exists(existingPath));

            Assert.Equal(new[] { "PROJREF|" + existingPath }, result);
        }

        [Fact]
        public void ProbeOrder_FirstMatchWins()
        {
            // All four candidates exist; candidate 1 (parent/PKG.csproj) must win.
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var c1 = "/root/sheet/Core.Swift.iOS.csproj";
            var c2 = "/root/Core.Swift.iOS/Core.Swift.iOS.csproj";
            var c3 = "/root/Core/Core.Swift.iOS.csproj";
            var c4 = "/root/Core.Swift.iOS.csproj";

            var result = Resolve(spec, "", Exists(c1, c2, c3, c4));

            Assert.Equal(new[] { "PROJREF|" + c1 }, result);
        }

        [Fact]
        public void ProbeOrder_SkipsMissingEarlierCandidates()
        {
            // Only candidate 3 exists; 1 and 2 miss, so 3 is selected over 4.
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var c3 = "/root/Core/Core.Swift.iOS.csproj";
            var c4 = "/root/Core.Swift.iOS.csproj";

            var result = Resolve(spec, "", Exists(c3, c4));

            Assert.Equal(new[] { "PROJREF|" + c3 }, result);
        }

        [Fact]
        public void Unresolved_EmitsWarnWithRawFields()
        {
            var spec = "Missing|Missing.Pkg|2.3.4|/root/sheet/X.xcframework";

            var result = Resolve(spec, "", Exists(/* nothing */));

            Assert.Equal(new[] { "WARN|Missing|Missing.Pkg|2.3.4|/root/sheet/X.xcframework" }, result);
        }

        [Fact]
        public void Warn_PreservesOriginalPercentEncodedFields()
        {
            // The four trailing WARN fields must round-trip the ORIGINAL encoded text so
            // SWIFTBIND080 can reconstruct the user's values; they are NOT decoded.
            var spec = "Mod%7CName|Pkg%3BId|1%250|/no/where/Z.xcframework";

            var result = Resolve(spec, "", Exists());

            Assert.Equal(new[] { "WARN|Mod%7CName|Pkg%3BId|1%250|/no/where/Z.xcframework" }, result);
        }

        [Fact]
        public void Decode_TranslatesPipeSemicolonPercent_ForProbing()
        {
            // Encoded module/pkg/xcfw decode before probing. A pipe-bearing package id
            // "Weird|Pkg" encodes as "Weird%7CPkg" and must probe with the literal pipe.
            var spec = "Mod|Weird%7CPkg|1.0.0|/root/sheet/Sheet.xcframework";
            var hit = "/root/sheet/Weird|Pkg.csproj"; // parent/<decoded-pkg>.csproj

            var result = Resolve(spec, "", Exists(hit));

            Assert.Equal(new[] { "PROJREF|" + hit }, result);
        }

        [Fact]
        public void Decode_PercentLast_So_DoubleEncodedPipe_StaysLiteral()
        {
            // "%257C" must decode to the literal text "%7C" (i.e. %25 -> % applied LAST),
            // NOT to a pipe. Probe a package id whose decoded form is "Pkg%7C".
            var spec = "Mod|Pkg%257C|1.0.0|/root/sheet/Sheet.xcframework";
            var hit = "/root/sheet/Pkg%7C.csproj";

            var result = Resolve(spec, "", Exists(hit));

            Assert.Equal(new[] { "PROJREF|" + hit }, result);
        }

        [Fact]
        public void Dedup_SkipsModuleAlreadyDeclaredExplicitly()
        {
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var hit = "/root/sheet/Core.Swift.iOS.csproj";

            // Even though the sibling exists, an explicit "Core" dep suppresses injection.
            var result = Resolve(spec, explicitDeps: "Core", Exists(hit));

            Assert.Empty(result);
        }

        [Fact]
        public void Dedup_WholeNameOnly_DoesNotSuppressPrefix()
        {
            // An explicit "Core" must not suppress a different module "CoreExtras".
            var spec = "CoreExtras|CoreExtras.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var hit = "/root/sheet/CoreExtras.Swift.iOS.csproj";

            var result = Resolve(spec, explicitDeps: "Core;Other", Exists(hit));

            Assert.Equal(new[] { "PROJREF|" + hit }, result);
        }

        [Fact]
        public void EmptyModuleField_RecordIsSkipped()
        {
            // Leading ';' produces an empty record; a record whose module field is empty
            // is skipped (mirrors the shell's `[ -z "$MOD_NAME" ] && continue`).
            var spec = ";|Pkg|1.0.0|/root/x.xcframework;Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var hit = "/root/sheet/Core.Swift.iOS.csproj";

            var result = Resolve(spec, "", Exists(hit));

            Assert.Equal(new[] { "PROJREF|" + hit }, result);
        }

        [Fact]
        public void MultipleRecords_PreserveInputOrder()
        {
            var a = "A|A.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var b = "Bmissing|B.Pkg|1.0.0|/root/sheet/Sheet.xcframework";
            var c = "C|C.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var spec = $"{a};{b};{c}";
            var aHit = "/root/sheet/A.Swift.iOS.csproj";
            var cHit = "/root/sheet/C.Swift.iOS.csproj";

            var result = Resolve(spec, "", Exists(aHit, cHit));

            Assert.Equal(new[]
            {
                "PROJREF|" + aHit,
                "WARN|Bmissing|B.Pkg|1.0.0|/root/sheet/Sheet.xcframework",
                "PROJREF|" + cHit,
            }, result);
        }

        [Fact]
        public void FewerThanFourFields_TreatsMissingAsEmpty()
        {
            // "Mod" alone: pkg/ver/xcfw empty. xcfw="" -> dirname is empty -> no probe hits.
            var result = Resolve("Mod", "", Exists());

            Assert.Equal(new[] { "WARN|Mod|||" }, result);
        }

        [Fact]
        public void OverlongRecord_PutsRemainderInXcframeworkField()
        {
            // Mirrors `read -r a b c d` putting the unsplit tail (incl. '|') into the last var.
            // Field 4 keeps "/root/a|b.xcframework"; decoded that path won't probe-hit, so WARN
            // echoes the raw tail verbatim.
            var spec = "Mod|Pkg|1.0.0|/root/a|b.xcframework";

            var result = Resolve(spec, "", Exists());

            Assert.Equal(new[] { "WARN|Mod|Pkg|1.0.0|/root/a|b.xcframework" }, result);
        }

        [Fact]
        public void DirName_BasenameOnlyXcframework_ProbesRelativeToCurrentDir()
        {
            // POSIX `dirname X.xcframework` is ".", so the candidate probes resolve relative to
            // the current dir ("./<pkg>.csproj"), not "/<pkg>.csproj". Shell-parity edge case.
            var spec = "Core|Core.Swift.iOS|1.0.0|Sheet.xcframework";
            var hit = "./Core.Swift.iOS.csproj";

            var result = Resolve(spec, "", Exists(hit));

            Assert.Equal(new[] { "PROJREF|" + hit }, result);
        }

        [Fact]
        public void DirName_TrailingSlashXcframework_StripsFinalComponentLikeDirname()
        {
            // POSIX `dirname /root/sheet/Sheet.xcframework/` strips the trailing slash + final
            // component to "/root/sheet" — NOT Path.GetDirectoryName's "/root/sheet/Sheet.xcframework"
            // (which would keep the slash-terminated path as its own directory and miss the sibling).
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework/";
            var hit = "/root/sheet/Core.Swift.iOS.csproj";

            var result = Resolve(spec, "", Exists(hit));

            Assert.Equal(new[] { "PROJREF|" + hit }, result);
        }

        [Fact]
        public void ToAbsolutePath_IsAppliedToProjRefLine()
        {
            // The matched candidate is normalized through toAbsolutePath before emission.
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var hit = "/root/sheet/Core.Swift.iOS.csproj";

            var result = Resolve(spec, "", Exists(hit), toAbs: _ => "/ABS/Core.Swift.iOS.csproj");

            Assert.Equal(new[] { "PROJREF|/ABS/Core.Swift.iOS.csproj" }, result);
        }

        [Fact]
        public void ExplicitDeps_NullOrEmpty_DoesNotThrow_AndResolvesNormally()
        {
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var hit = "/root/sheet/Core.Swift.iOS.csproj";

            var r1 = Resolve(spec, null, Exists(hit));
            var r2 = Resolve(spec, "", Exists(hit));

            Assert.Equal(new[] { "PROJREF|" + hit }, r1);
            Assert.Equal(new[] { "PROJREF|" + hit }, r2);
        }
    }
}
