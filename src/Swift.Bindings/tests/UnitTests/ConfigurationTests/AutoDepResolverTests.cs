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
    /// former inline POSIX-sh cross-module dependency resolver. These pin the behavior the
    /// migrated <c>_ResolveSwiftAutoDetectedDependencies</c>
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

        // ── Probe 5: name-independent sibling-binding-project lookup ──────────────────
        //
        // The PackageId in a dependency record is SYNTHESIZED (`{Module}.Swift.{Platform}`) because
        // an auto-detected dependency only ever carries a module name and an xcframework path. The
        // four name-derived probes above are therefore blind to any repo that names its binding
        // projects differently — the real shape being `FBAEMKit/SwiftBindings.Facebook.AEM.csproj`
        // next to `FBAEMKit.xcframework`. Probe 5 looks in the dependency xcframework's OWN
        // directory and identifies the binding project by CONTENT, so a satisfied dependency
        // closure stops reporting itself as unresolved.

        // Directory listing that "contains" exactly the supplied files, keyed by directory.
        private static Func<string, IReadOnlyList<string>> Listing(params (string Dir, string[] Files)[] dirs)
        {
            var map = dirs.ToDictionary(d => d.Dir, d => (IReadOnlyList<string>)d.Files, StringComparer.Ordinal);
            return dir => map.TryGetValue(dir, out var files) ? files : Array.Empty<string>();
        }

        private const string BindingProjectXml = "<Project Sdk=\"SwiftBindings.Sdk/0.18.0\"></Project>";
        private const string PlainProjectXml = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";

        private static Func<string, string?> Contents(params (string Path, string Text)[] files)
        {
            var map = files.ToDictionary(f => f.Path, f => f.Text, StringComparer.Ordinal);
            return path => map.TryGetValue(path, out var text) ? text : null;
        }

        [Fact]
        public void Probe5_FindsDifferentlyNamedSiblingBindingProject()
        {
            // The synthesized package id (FBAEMKit.Swift.iOS) matches nothing on disk; the project
            // that actually binds this xcframework is named after its NuGet package instead.
            var spec = "FBAEMKit|FBAEMKit.Swift.iOS|0.0.0|/repo/FBAEMKit/FBAEMKit.xcframework";
            var real = "/repo/FBAEMKit/SwiftBindings.Facebook.AEM.csproj";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(), Identity,
                Listing(("/repo/FBAEMKit", new[] { real })),
                Contents((real, BindingProjectXml))).ToList();

            Assert.Equal(new[] { "PROJREF|" + real }, result);
        }

        [Fact]
        public void Probe5_RunsOnlyAfterTheFourNameDerivedProbesMiss()
        {
            // A conventionally-named sibling still wins: the frozen probe order is unchanged and
            // probe 5 is strictly a fallback.
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var conventional = "/root/sheet/Core.Swift.iOS.csproj";
            var other = "/root/sheet/SwiftBindings.Core.csproj";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(conventional), Identity,
                Listing(("/root/sheet", new[] { other })),
                Contents((other, BindingProjectXml))).ToList();

            Assert.Equal(new[] { "PROJREF|" + conventional }, result);
        }

        [Fact]
        public void Probe5_IgnoresNonBindingProjectsInTheSameDirectory()
        {
            // An app/test/tool csproj next to a vendored xcframework must never be mistaken for the
            // dependency's binding — the marker is the SwiftBindings.Sdk declaration, not proximity.
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var app = "/root/sheet/SomeApp.csproj";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(), Identity,
                Listing(("/root/sheet", new[] { app })),
                Contents((app, PlainProjectXml))).ToList();

            Assert.Equal(new[] { "WARN|Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework" }, result);
        }

        [Fact]
        public void Probe5_AmbiguousDirectory_WarnsRatherThanGuessing()
        {
            // Two binding projects in one directory: there is no evidence which one binds this
            // xcframework, and a wrong ProjectReference is worse than a warning. Fail closed.
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var a = "/root/sheet/SwiftBindings.A.csproj";
            var b = "/root/sheet/SwiftBindings.B.csproj";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(), Identity,
                Listing(("/root/sheet", new[] { a, b })),
                Contents((a, BindingProjectXml), (b, BindingProjectXml))).ToList();

            Assert.Equal(new[] { "WARN|Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework" }, result);
        }

        [Fact]
        public void Probe5_UnreadableCandidate_IsNotAMatch()
        {
            // readFileText returns null on I/O failure; a candidate we cannot read is not evidence.
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var unreadable = "/root/sheet/SwiftBindings.Core.csproj";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(), Identity,
                Listing(("/root/sheet", new[] { unreadable })),
                Contents(/* nothing readable */)).ToList();

            Assert.Equal(new[] { "WARN|Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework" }, result);
        }

        [Fact]
        public void Probe5_LooksOnlyInTheXcframeworksOwnDirectory()
        {
            // The grandparent is NOT scanned: a repo root holding one binding project would
            // otherwise resolve every unrelated dependency to it.
            var spec = "Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework";
            var upOne = "/root/SwiftBindings.Core.csproj";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(), Identity,
                Listing(("/root", new[] { upOne })),
                Contents((upOne, BindingProjectXml))).ToList();

            Assert.Equal(new[] { "WARN|Core|Core.Swift.iOS|1.0.0|/root/sheet/Sheet.xcframework" }, result);
        }

        [Fact]
        public void Probe5_Disabled_WhenNoListingDelegateIsInjected()
        {
            // The four-argument overload keeps the pre-existing behavior exactly: no directory
            // scanning, so a differently-named sibling still warns.
            var spec = "FBAEMKit|FBAEMKit.Swift.iOS|0.0.0|/repo/FBAEMKit/FBAEMKit.xcframework";

            var result = Resolve(spec, "", Exists("/repo/FBAEMKit/SwiftBindings.Facebook.AEM.csproj"));

            Assert.Equal(new[] { "WARN|FBAEMKit|FBAEMKit.Swift.iOS|0.0.0|/repo/FBAEMKit/FBAEMKit.xcframework" }, result);
        }

        [Fact]
        public void Probe5_DedupStillWins_ExplicitlyDeclaredModuleIsNeverProbed()
        {
            var spec = "FBAEMKit|FBAEMKit.Swift.iOS|0.0.0|/repo/FBAEMKit/FBAEMKit.xcframework";
            var real = "/repo/FBAEMKit/SwiftBindings.Facebook.AEM.csproj";

            var result = AutoDepResolver.Resolve(
                spec, explicitDeps: "FBAEMKit", Exists(), Identity,
                Listing(("/repo/FBAEMKit", new[] { real })),
                Contents((real, BindingProjectXml))).ToList();

            Assert.Empty(result);
        }

        [Fact]
        public void Probe5_ResultIsNormalizedThroughToAbsolutePath()
        {
            var spec = "FBAEMKit|FBAEMKit.Swift.iOS|0.0.0|/repo/FBAEMKit/FBAEMKit.xcframework";
            var real = "/repo/FBAEMKit/SwiftBindings.Facebook.AEM.csproj";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(), _ => "/ABS/AEM.csproj",
                Listing(("/repo/FBAEMKit", new[] { real })),
                Contents((real, BindingProjectXml))).ToList();

            Assert.Equal(new[] { "PROJREF|/ABS/AEM.csproj" }, result);
        }

        [Theory]
        [InlineData("<Project Sdk=\"SwiftBindings.Sdk/0.18.0\"></Project>", true)]
        [InlineData("<Project Sdk='SwiftBindings.Sdk'></Project>", true)]
        [InlineData("<Project><Sdk Name=\"SwiftBindings.Sdk\" Version=\"0.18.0\" /></Project>", true)]
        // A bare mention in a path/comment is NOT a declaration — this is the false-positive shape
        // the attribute-form markers exist to reject.
        [InlineData("<Project><Import Project=\"/x/SwiftBindings.Sdk/Sdk.props\" /></Project>", false)]
        [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", false)]
        // ── Cases the old substring markers got WRONG, now decided by a real XML parse ──
        // A commented-out declaration is not a declaration. The substring markers matched it.
        [InlineData("<!-- <Project Sdk=\"SwiftBindings.Sdk/0.18.0\"> --><Project Sdk=\"Microsoft.NET.Sdk\"></Project>", false)]
        [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><!-- was Sdk=\"SwiftBindings.Sdk/0.18.0\" --></Project>", false)]
        // A different SDK that merely STARTS with our id is not our SDK. The substring markers matched it.
        [InlineData("<Project Sdk=\"SwiftBindings.SdkSomethingElse\"></Project>", false)]
        // Valid XML may put whitespace around '='. The substring markers MISSED this real binding project.
        [InlineData("<Project Sdk = \"SwiftBindings.Sdk/0.18.0\" ></Project>", true)]
        // The single-quoted <Sdk Name='…'/> spelling (marker existed, was never covered by a test).
        [InlineData("<Project><Sdk Name='SwiftBindings.Sdk' /></Project>", true)]
        // The Sdk attribute is a ';'-delimited list; our SDK anywhere in it counts.
        [InlineData("<Project Sdk=\"Microsoft.NET.Sdk;SwiftBindings.Sdk/0.18.0\"></Project>", true)]
        // The explicit-import form is a real SDK declaration (and one the previous substring
        // markers accepted) — rejecting it would turn a resolvable dependency back into a warning.
        [InlineData("<Project><Import Project=\"Sdk.props\" Sdk=\"SwiftBindings.Sdk\" /></Project>", true)]
        // The legacy 2003 xmlns is still valid MSBuild and must not change the answer.
        [InlineData("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" Sdk=\"SwiftBindings.Sdk\"></Project>", true)]
        // Fail closed on anything we cannot parse or that is not a project at all.
        [InlineData("<Project Sdk=\"SwiftBindings.Sdk\"", false)]
        [InlineData("<Sdk Name=\"SwiftBindings.Sdk\" />", false)]
        public void Probe5_BindingProjectMarker_MatchesOnlyTheSdkDeclaration(string csprojText, bool expectMatch)
        {
            var path = "/root/sheet/Candidate.csproj";

            var found = AutoDepResolver.ProbeSiblingBindingProject(
                "/root/sheet",
                Listing(("/root/sheet", new[] { path })),
                Contents((path, csprojText)));

            Assert.Equal(expectMatch ? path : null, found);
        }

        [Fact]
        public void Probe5_ExcludesTheProjectBeingBuilt_NeverSelfReferences()
        {
            // A vendor can drop several xcframeworks beside ONE binding project, so an
            // auto-detected dependency's directory can be the consumer's own directory. Without
            // the exclusion the consumer is the sole content match and probe 5 injects a
            // self-ProjectReference (an MSBuild circular reference). Warn instead.
            var self = "/repo/Facebook/SwiftBindings.Facebook.Core.csproj";
            var spec = "FBSDKCoreKit_Basics|FBSDKCoreKit_Basics.Swift.iOS|0.0.0|/repo/Facebook/FBSDKCoreKit_Basics.xcframework";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(), Identity,
                Listing(("/repo/Facebook", new[] { self })),
                Contents((self, BindingProjectXml)),
                consumerProjectPath: self).ToList();

            Assert.Equal(
                new[] { "WARN|FBSDKCoreKit_Basics|FBSDKCoreKit_Basics.Swift.iOS|0.0.0|/repo/Facebook/FBSDKCoreKit_Basics.xcframework" },
                result);
        }

        [Fact]
        public void Probe5_ExcludingTheProjectBeingBuilt_StillFindsARealSiblingInTheSameDirectory()
        {
            // The exclusion removes exactly one candidate — it must not disable the probe, and it
            // must not make a genuine two-project directory look ambiguous.
            var self = "/repo/Facebook/SwiftBindings.Facebook.Core.csproj";
            var sibling = "/repo/Facebook/SwiftBindings.Facebook.CoreBasics.csproj";
            var spec = "FBSDKCoreKit_Basics|FBSDKCoreKit_Basics.Swift.iOS|0.0.0|/repo/Facebook/FBSDKCoreKit_Basics.xcframework";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(), Identity,
                Listing(("/repo/Facebook", new[] { self, sibling })),
                Contents((self, BindingProjectXml), (sibling, BindingProjectXml)),
                consumerProjectPath: self).ToList();

            Assert.Equal(new[] { "PROJREF|" + sibling }, result);
        }

        [Fact]
        public void Probe5_ConsumerExclusion_ComparesNormalizedPaths()
        {
            // MSBuild hands us $(MSBuildProjectFullPath); the enumerator yields whatever the
            // filesystem spells. The exclusion compares both through toAbsolutePath, so an
            // un-normalized spelling of the same file still excludes it.
            var self = "/repo/Facebook/SwiftBindings.Facebook.Core.csproj";
            var selfAsGiven = "/repo/Facebook/./SwiftBindings.Facebook.Core.csproj";
            var spec = "Dep|Dep.Swift.iOS|0.0.0|/repo/Facebook/Dep.xcframework";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(),
                toAbsolutePath: p => p.Replace("/./", "/", StringComparison.Ordinal),
                Listing(("/repo/Facebook", new[] { self })),
                Contents((self, BindingProjectXml)),
                consumerProjectPath: selfAsGiven).ToList();

            Assert.Equal(new[] { "WARN|Dep|Dep.Swift.iOS|0.0.0|/repo/Facebook/Dep.xcframework" }, result);
        }

        [Fact]
        public void Probe5_ADifferentBindingProjectBesideTheXcframework_IsStillAccepted()
        {
            // Pins a DISMISSED-BY-DESIGN behavior (src/docs/not-planned.md): the probe proves the
            // candidate is a binding project, not that it binds THIS xcframework. A lone unrelated
            // binding project beside a vendored dependency is therefore accepted. Requiring linking
            // evidence would reject the auto-discovery shape the probe exists to serve, and a wrong
            // hit fails visibly at build (the referenced project does not carry the types the
            // generated code names) rather than silently. Change this test only with that decision.
            var unrelated = "/repo/vendor/SwiftBindings.SomethingElse.csproj";
            var spec = "Dep|Dep.Swift.iOS|0.0.0|/repo/vendor/Dep.xcframework";

            var result = AutoDepResolver.Resolve(
                spec, "", Exists(), Identity,
                Listing(("/repo/vendor", new[] { unrelated })),
                Contents((unrelated, BindingProjectXml))).ToList();

            Assert.Equal(new[] { "PROJREF|" + unrelated }, result);
        }
    }
}
