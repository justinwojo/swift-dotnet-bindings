// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Covers the consumer-side step that gives frameworks embedded in a macOS / Mac Catalyst app
    /// bundle Apple's versioned (deep) layout before the bundle is signed.
    ///
    /// Two layers are asserted, because the correctness argument spans both:
    ///   * the shell converter itself — shallow bundles become versioned, a versioned tree whose
    ///     links were flattened is repaired, an already-correct bundle is left byte-for-byte alone,
    ///     and an unfamiliar shape is skipped rather than restructured on a guess;
    ///   * the MSBuild targets around it — the guards that keep the step on macOS / Mac Catalyst
    ///     app bundles only. iOS and tvOS frameworks are required to be shallow, so "we do not
    ///     touch iOS" is a property of those conditions, not of the converter.
    ///
    /// The converter is exercised through `sh` on temp trees, the way the build invokes it.
    /// </summary>
    public class MacFrameworkAnatomyTests : IDisposable
    {
        private readonly List<string> _tempDirs = new();

        private static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public void Dispose()
        {
            foreach (var dir in _tempDirs)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private string MakeTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"mac_anatomy_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);
            return dir;
        }

        private static string FindRepoRoot()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                var gitPath = Path.Combine(dir, ".git");
                // .git is a directory in normal repos, a file in worktrees.
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Cannot find repo root.");
        }

        private static string BuildDir =>
            Path.Combine(FindRepoRoot(), "src", "Swift.Runtime", "src", "build");

        private static string ScriptPath => Path.Combine(BuildDir, "deepen-mac-framework.sh");

        private static string TargetsPath =>
            Path.Combine(BuildDir, "SwiftBindings.MacFrameworkAnatomy.targets");

        // ── helpers ───────────────────────────────────────────────────────────────────────────

        private static (int exitCode, string stdout, string stderr) Run(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stdout, stderr);
        }

        private static (int exitCode, string stdout, string stderr) Deepen(string frameworksDir, params string[] extra)
        {
            var args = new List<string> { ScriptPath };
            args.AddRange(extra);
            args.Add(frameworksDir);
            return Run("/bin/sh", args.ToArray());
        }

        // LinkTarget is the target as written on disk, which is what "relative links only" is about;
        // ResolveLinkTarget would hand back an absolute path and hide that distinction.
        private static string? LinkTarget(string path) => new FileInfo(path).LinkTarget;

        private static bool IsSymlink(string path) => LinkTarget(path) is not null;

        private static string PlistXml(string executable) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\"><dict>\n" +
            $"  <key>CFBundleExecutable</key><string>{executable}</string>\n" +
            "  <key>CFBundleIdentifier</key><string>com.example.fixture</string>\n" +
            "</dict></plist>\n";

        /// <summary>The shape our producers ship on every slice: binary + Info.plist at the root.</summary>
        private static string CreateShallowFramework(
            string frameworksDir, string name, string? executableName = null, bool withModules = true)
        {
            var exec = executableName ?? name;
            var fw = Path.Combine(frameworksDir, name + ".framework");
            Directory.CreateDirectory(fw);
            File.WriteAllText(Path.Combine(fw, exec), "stub-mach-o");
            File.WriteAllText(Path.Combine(fw, "Info.plist"), PlistXml(exec));
            if (withModules)
            {
                Directory.CreateDirectory(Path.Combine(fw, "Modules"));
                File.WriteAllText(Path.Combine(fw, "Modules", "module.modulemap"), "framework module X {}");
            }
            return fw;
        }

        /// <summary>A correct versioned bundle, as the converter should leave one.</summary>
        private static string CreateDeepFramework(string frameworksDir, string name)
        {
            var fw = Path.Combine(frameworksDir, name + ".framework");
            var versionA = Path.Combine(fw, "Versions", "A");
            Directory.CreateDirectory(Path.Combine(versionA, "Resources"));
            File.WriteAllText(Path.Combine(versionA, name), "stub-mach-o");
            File.WriteAllText(Path.Combine(versionA, "Resources", "Info.plist"), PlistXml(name));
            Directory.CreateSymbolicLink(Path.Combine(fw, "Versions", "Current"), "A");
            Directory.CreateSymbolicLink(Path.Combine(fw, name), $"Versions/Current/{name}");
            Directory.CreateSymbolicLink(Path.Combine(fw, "Resources"), "Versions/Current/Resources");
            return fw;
        }

        /// <summary>
        /// The failure mode a copier that follows links produces: a real Versions/ tree, but
        /// Versions/Current and the root entries are duplicated files instead of links.
        /// </summary>
        private static string CreateFlattenedDeepFramework(string frameworksDir, string name)
        {
            var fw = Path.Combine(frameworksDir, name + ".framework");
            var versionA = Path.Combine(fw, "Versions", "A");
            Directory.CreateDirectory(Path.Combine(versionA, "Resources"));
            File.WriteAllText(Path.Combine(versionA, name), "stub-mach-o");
            File.WriteAllText(Path.Combine(versionA, "Resources", "Info.plist"), PlistXml(name));

            // Versions/Current copied rather than linked.
            var current = Path.Combine(fw, "Versions", "Current");
            Directory.CreateDirectory(Path.Combine(current, "Resources"));
            File.WriteAllText(Path.Combine(current, name), "stub-mach-o");
            File.WriteAllText(Path.Combine(current, "Resources", "Info.plist"), PlistXml(name));

            // Root entries copied rather than linked.
            File.WriteAllText(Path.Combine(fw, name), "stub-mach-o");
            Directory.CreateDirectory(Path.Combine(fw, "Resources"));
            File.WriteAllText(Path.Combine(fw, "Resources", "Info.plist"), PlistXml(name));
            return fw;
        }

        private static void AssertValidDeepBundle(string fw, string execName)
        {
            var name = Path.GetFileName(fw);

            Assert.True(IsSymlink(Path.Combine(fw, "Versions", "Current")),
                $"{name}: Versions/Current must be a real symbolic link.");
            Assert.Equal("A", LinkTarget(Path.Combine(fw, "Versions", "Current")));

            Assert.True(IsSymlink(Path.Combine(fw, execName)),
                $"{name}: the top-level executable must be a symbolic link into Versions/Current.");
            Assert.Equal($"Versions/Current/{execName}", LinkTarget(Path.Combine(fw, execName)));

            Assert.True(File.Exists(Path.Combine(fw, "Versions", "A", execName)),
                $"{name}: the executable must live at Versions/A/{execName}.");
            Assert.True(File.Exists(Path.Combine(fw, "Versions", "A", "Resources", "Info.plist")),
                $"{name}: Info.plist must live at Versions/A/Resources/Info.plist.");

            // Resolving through the links is what a validator (and dyld) actually does.
            Assert.True(File.Exists(Path.Combine(fw, "Resources", "Info.plist")),
                $"{name}: Resources/Info.plist must resolve through Versions/Current.");

            // Root holds Versions plus links, nothing else.
            foreach (var entry in Directory.GetFileSystemEntries(fw))
            {
                var entryName = Path.GetFileName(entry);
                if (entryName == "Versions") continue;
                Assert.True(IsSymlink(entry),
                    $"{name}: unexpected real entry at the bundle root: {entryName}. Only Versions/ and symbolic links belong there.");
                // Relative links only, so the bundle stays relocatable.
                Assert.False(Path.IsPathRooted(LinkTarget(entry)!),
                    $"{name}: {entryName} must be a relative link, got '{LinkTarget(entry)}'.");
            }

            Assert.False(Directory.Exists(Path.Combine(fw, "_CodeSignature")),
                $"{name}: a root _CodeSignature describes the shallow layout and must not survive the rewrite.");
        }

        // ── converter behaviour ───────────────────────────────────────────────────────────────

        [Fact]
        public void Shallow_IsRewrittenIntoAVersionedBundle()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateShallowFramework(frameworks, "SwiftBindingsRuntime");
            // A stale seal of the shallow layout, exactly what the workload's embed copies over.
            Directory.CreateDirectory(Path.Combine(fw, "_CodeSignature"));
            File.WriteAllText(Path.Combine(fw, "_CodeSignature", "CodeResources"), "<plist/>");

            var (exit, stdout, stderr) = Deepen(frameworks);
            Assert.True(exit == 0, $"converter failed: {stdout}{stderr}");

            AssertValidDeepBundle(fw, "SwiftBindingsRuntime");

            // Non-plist directories keep their name under the version directory and gain a root link.
            Assert.True(File.Exists(Path.Combine(fw, "Versions", "A", "Modules", "module.modulemap")));
            Assert.True(IsSymlink(Path.Combine(fw, "Modules")));
        }

        [Fact]
        public void Shallow_LooseResourceFilesMoveUnderResources()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateShallowFramework(frameworks, "Vendor");
            // A privacy manifest is read from inside the embedded bundle, so it has to survive.
            File.WriteAllText(Path.Combine(fw, "PrivacyInfo.xcprivacy"), "<plist/>");
            // An existing Resources directory travels whole rather than being rearranged.
            Directory.CreateDirectory(Path.Combine(fw, "Resources"));
            File.WriteAllText(Path.Combine(fw, "Resources", "asset.txt"), "payload");

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "Vendor");
            Assert.True(File.Exists(Path.Combine(fw, "Versions", "A", "Resources", "PrivacyInfo.xcprivacy")));
            Assert.True(File.Exists(Path.Combine(fw, "Versions", "A", "Resources", "asset.txt")));
            Assert.Equal("payload", File.ReadAllText(Path.Combine(fw, "Resources", "asset.txt")));
        }

        [Fact]
        public void Shallow_HonoursCFBundleExecutableWhenItDiffersFromTheBundleName()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateShallowFramework(frameworks, "Bundle", executableName: "DifferentBinary");

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "DifferentBinary");
        }

        [Fact]
        public void FlattenedVersionedTree_IsRepairedInPlace()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateFlattenedDeepFramework(frameworks, "SBApple");

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "SBApple");
            // Repair, not duplication: the payload stays in the single existing version directory.
            Assert.Equal(new[] { "A", "Current" },
                Directory.GetFileSystemEntries(Path.Combine(fw, "Versions"))
                    .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void NewerContentDeliveredAtTheRoot_ReplacesTheVersionDirectorysCopy()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateDeepFramework(frameworks, "SBApple");
            Directory.CreateDirectory(Path.Combine(fw, "Versions", "A", "Modules"));
            File.WriteAllText(Path.Combine(fw, "Versions", "A", "Modules", "module.modulemap"), "old-module");
            Directory.CreateSymbolicLink(Path.Combine(fw, "Modules"), "Versions/Current/Modules");

            // What a copier that overwrites file links with files (ditto does exactly this) leaves
            // behind when a newer package is copied over the rewritten bundle: fresh content at the
            // root, previous content under Versions/. The root is the newer delivery and must win.
            File.Delete(Path.Combine(fw, "SBApple"));
            File.WriteAllText(Path.Combine(fw, "SBApple"), "new-mach-o");
            File.Delete(Path.Combine(fw, "Resources"));
            Directory.CreateDirectory(Path.Combine(fw, "Resources"));
            File.WriteAllText(Path.Combine(fw, "Resources", "Info.plist"), PlistXml("SBApple") + "<!-- new -->");
            File.Delete(Path.Combine(fw, "Modules"));
            Directory.CreateDirectory(Path.Combine(fw, "Modules"));
            File.WriteAllText(Path.Combine(fw, "Modules", "module.modulemap"), "new-module");

            var (exit, stdout, stderr) = Deepen(frameworks);
            Assert.True(exit == 0, $"converter failed: {stdout}{stderr}");

            AssertValidDeepBundle(fw, "SBApple");
            Assert.Equal("new-mach-o", File.ReadAllText(Path.Combine(fw, "Versions", "A", "SBApple")));
            Assert.EndsWith("<!-- new -->", File.ReadAllText(Path.Combine(fw, "Versions", "A", "Resources", "Info.plist")));
            Assert.Equal("new-module", File.ReadAllText(Path.Combine(fw, "Versions", "A", "Modules", "module.modulemap")));
        }

        [Fact]
        public void AlreadyVersioned_IsLeftByteIdentical()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateDeepFramework(frameworks, "Runtime");

            var before = Snapshot(fw);
            Assert.Equal(0, Deepen(frameworks).exitCode);
            var after = Snapshot(fw);

            Assert.Equal(before, after);
            AssertValidDeepBundle(fw, "Runtime");
        }

        [Fact]
        public void Rewrite_IsIdempotentAcrossRepeatedBuilds()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateShallowFramework(frameworks, "Runtime");

            Assert.Equal(0, Deepen(frameworks).exitCode);
            var afterFirst = Snapshot(fw);

            Assert.Equal(0, Deepen(frameworks).exitCode);
            Assert.Equal(afterFirst, Snapshot(fw));
            AssertValidDeepBundle(fw, "Runtime");
        }

        // ── damaged and half-finished bundles ─────────────────────────────────────────────────
        //
        // A build that dies between the first move and the last link leaves the bundle in a state
        // that is neither shallow nor valid, and the next build has to finish the job. The cases
        // below walk the states an interruption (or a copier that mangled the links in transit)
        // can leave behind; every one of them has to converge on the same valid deep bundle.

        [Fact]
        public void DanglingCurrentLink_IsRepointedAtTheRealVersionDirectory()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateDeepFramework(frameworks, "Runtime");

            // Current survives as a link but names a version directory that is not there.
            File.Delete(Path.Combine(fw, "Versions", "Current"));
            Directory.CreateSymbolicLink(Path.Combine(fw, "Versions", "Current"), "Missing");

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "Runtime");
        }

        [Fact]
        public void RootLinkWithTheWrongTarget_IsRepointedThroughCurrent()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateDeepFramework(frameworks, "Runtime");

            // Links that bypass Current, or point somewhere else entirely, are still links — but
            // the layout is defined by where they point, not by their being links.
            File.Delete(Path.Combine(fw, "Runtime"));
            Directory.CreateSymbolicLink(Path.Combine(fw, "Runtime"), "Versions/A/Runtime");
            Directory.Delete(Path.Combine(fw, "Resources"));
            Directory.CreateSymbolicLink(Path.Combine(fw, "Resources"), "Versions/Current/Wrong");

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "Runtime");
            Assert.Equal("Versions/Current/Resources", LinkTarget(Path.Combine(fw, "Resources")));
        }

        [Fact]
        public void InterruptedRewrite_EmptyVersionDirectoryOverAShallowRoot_Converges()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateShallowFramework(frameworks, "Runtime");
            // The state a build leaves behind if it dies immediately after making the version
            // directory: the payload is all still at the root.
            Directory.CreateDirectory(Path.Combine(fw, "Versions", "A"));

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "Runtime");
            Assert.True(File.Exists(Path.Combine(fw, "Versions", "A", "Modules", "module.modulemap")));
        }

        [Fact]
        public void InterruptedRewrite_ExecutableMovedButNothingElse_Converges()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateShallowFramework(frameworks, "Runtime");
            var versionA = Path.Combine(fw, "Versions", "A");
            Directory.CreateDirectory(versionA);
            // One move in: the binary is under the version directory, the plist and the rest of
            // the payload are still at the root, and there is no Current link yet.
            File.Move(Path.Combine(fw, "Runtime"), Path.Combine(versionA, "Runtime"));

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "Runtime");
            Assert.True(File.Exists(Path.Combine(fw, "Versions", "A", "Modules", "module.modulemap")));
        }

        [Fact]
        public void InterruptedRewrite_RealExecutableLeftAtTheRootOfAVersionedTree_Converges()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateDeepFramework(frameworks, "Runtime");
            // A finished-looking tree whose root executable is a real file rather than a link:
            // the shape a half-run rewrite or a link-flattening copier leaves.
            File.Delete(Path.Combine(fw, "Runtime"));
            File.WriteAllText(Path.Combine(fw, "Runtime"), "stub-mach-o");
            // And a stray file that belongs under Resources, plus a seal of the shallow layout.
            File.WriteAllText(Path.Combine(fw, "PrivacyInfo.xcprivacy"), "<plist/>");
            Directory.CreateDirectory(Path.Combine(fw, "_CodeSignature"));
            File.WriteAllText(Path.Combine(fw, "_CodeSignature", "CodeResources"), "<plist/>");

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "Runtime");
            Assert.True(File.Exists(Path.Combine(fw, "Versions", "A", "Resources", "PrivacyInfo.xcprivacy")));
        }

        [Fact]
        public void OtherwiseValidDeepBundle_WithARealFileAtTheRoot_IsNotAcceptedAsIs()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateDeepFramework(frameworks, "Runtime");
            // Everything the early accept looks at is in place, so only a check that sweeps the
            // whole root notices this.
            File.WriteAllText(Path.Combine(fw, "leftover.txt"), "payload");

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "Runtime");
            Assert.Equal("payload",
                File.ReadAllText(Path.Combine(fw, "Versions", "A", "Resources", "leftover.txt")));
        }

        [Fact]
        public void UnreadableInfoPlist_FallsBackToTheBundleNameRatherThanADiagnostic()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = CreateShallowFramework(frameworks, "Runtime");
            // A plist the reader cannot parse. What matters is that the converter treats that as
            // "no answer" and falls back to the bundle name, rather than carrying the reader's
            // complaint forward as though it were the executable's name.
            File.WriteAllText(Path.Combine(fw, "Info.plist"), "<plist/>");

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "Runtime");
        }

        [Fact]
        public void FlattenedTreeWhoseOnlyVersionIsCurrent_KeepsThePayload()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var fw = Path.Combine(frameworks, "Runtime.framework");
            // A copier that followed links can land the payload in a real Versions/Current with no
            // sibling version directory at all. Discarding Current to make room for the link would
            // take the payload with it.
            var current = Path.Combine(fw, "Versions", "Current");
            Directory.CreateDirectory(Path.Combine(current, "Resources"));
            File.WriteAllText(Path.Combine(current, "Runtime"), "stub-mach-o");
            File.WriteAllText(Path.Combine(current, "Resources", "Info.plist"), PlistXml("Runtime"));
            File.WriteAllText(Path.Combine(current, "Resources", "asset.txt"), "payload");
            File.WriteAllText(Path.Combine(fw, "Runtime"), "stub-mach-o");

            Assert.Equal(0, Deepen(frameworks).exitCode);

            AssertValidDeepBundle(fw, "Runtime");
            Assert.Equal("payload", File.ReadAllText(Path.Combine(fw, "Resources", "asset.txt")));
        }

        [Fact]
        public void UnfamiliarBundleShape_IsLeftAlone()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();

            // A .framework directory with no executable at its root is not a shape we recognise;
            // restructuring it would be a guess.
            var odd = Path.Combine(frameworks, "Odd.framework");
            Directory.CreateDirectory(Path.Combine(odd, "Data"));
            File.WriteAllText(Path.Combine(odd, "Data", "payload.bin"), "x");

            // Nor is a Versions/ tree with no executable anywhere in it: the repair path finishes
            // a rewrite it can see the payload for, and this one has none to place.
            var hollow = Path.Combine(frameworks, "Hollow.framework");
            Directory.CreateDirectory(Path.Combine(hollow, "Versions", "A", "Resources"));
            File.WriteAllText(Path.Combine(hollow, "Versions", "A", "Resources", "asset.txt"), "z");

            // A sibling that is not a framework at all must be invisible to the step.
            var plain = Path.Combine(frameworks, "notaframework");
            Directory.CreateDirectory(plain);
            File.WriteAllText(Path.Combine(plain, "file.txt"), "y");

            var before = Snapshot(frameworks);
            Assert.Equal(0, Deepen(frameworks).exitCode);
            Assert.Equal(before, Snapshot(frameworks));
        }

        [Fact]
        public void ExcludedFramework_IsLeftAlone()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var kept = CreateShallowFramework(frameworks, "KeepShallow");
            var rewritten = CreateShallowFramework(frameworks, "Rewrite");

            var keptBefore = Snapshot(kept);
            Assert.Equal(0, Deepen(frameworks, "--exclude", "KeepShallow").exitCode);

            Assert.Equal(keptBefore, Snapshot(kept));
            AssertValidDeepBundle(rewritten, "Rewrite");
        }

        [Fact]
        public void NestedFrameworks_AreRewrittenAlongWithTheirParent()
        {
            if (!IsMacOS) return;

            // An umbrella or vendor framework carrying frameworks of its own: the child is as
            // visible to a validator as the parent, and after the parent's rewrite it lives under
            // Versions/A/Frameworks. Both a shallow parent and one that is already deep are covered,
            // since the already-deep path is the one an incremental rebuild takes.
            var frameworks = MakeTempDir();
            var shallowParent = CreateShallowFramework(frameworks, "Vendor", withModules: false);
            CreateShallowFramework(Path.Combine(shallowParent, "Frameworks"), "Child");

            var deepParent = CreateDeepFramework(frameworks, "Umbrella");
            var grandchildHost = CreateShallowFramework(Path.Combine(deepParent, "Versions", "A", "Frameworks"), "Inner");
            CreateShallowFramework(Path.Combine(grandchildHost, "Frameworks"), "Innermost", withModules: false);
            Directory.CreateSymbolicLink(Path.Combine(deepParent, "Frameworks"), "Versions/Current/Frameworks");

            var (exit, stdout, stderr) = Deepen(frameworks);
            Assert.True(exit == 0, $"converter failed: {stdout}{stderr}");

            AssertValidDeepBundle(shallowParent, "Vendor");
            AssertValidDeepBundle(Path.Combine(shallowParent, "Versions", "A", "Frameworks", "Child.framework"), "Child");
            Assert.True(IsSymlink(Path.Combine(shallowParent, "Frameworks")));

            AssertValidDeepBundle(deepParent, "Umbrella");
            var inner = Path.Combine(deepParent, "Versions", "A", "Frameworks", "Inner.framework");
            AssertValidDeepBundle(inner, "Inner");
            AssertValidDeepBundle(Path.Combine(inner, "Versions", "A", "Frameworks", "Innermost.framework"), "Innermost");

            // The nested passes report per bundle, never a spurious "no changes needed".
            Assert.DoesNotContain("no changes needed", stdout);
        }

        [Fact]
        public void NestedFrameworks_HonourTheExclusionList()
        {
            if (!IsMacOS) return;

            var frameworks = MakeTempDir();
            var parent = CreateShallowFramework(frameworks, "Vendor", withModules: false);
            var kept = CreateShallowFramework(Path.Combine(parent, "Frameworks"), "KeepShallow");
            var keptBefore = Snapshot(kept);

            Assert.Equal(0, Deepen(frameworks, "--exclude", "KeepShallow").exitCode);

            AssertValidDeepBundle(parent, "Vendor");
            var keptAfter = Path.Combine(parent, "Versions", "A", "Frameworks", "KeepShallow.framework");
            Assert.Equal(keptBefore, Snapshot(keptAfter));
        }

        [Fact]
        public void MissingFrameworksDirectory_IsANoOpNotAFailure()
        {
            if (!IsMacOS) return;

            // An app that embeds nothing still runs the step; it must not fail the build.
            var (exit, _, _) = Deepen(Path.Combine(MakeTempDir(), "Frameworks"));
            Assert.Equal(0, exit);
        }

        // A comparable view of a tree: relative path, entry kind, and either the link target or the
        // file's bytes. Any move, relink, or content change shows up as a difference.
        private static List<string> Snapshot(string root)
        {
            var entries = new List<string>();
            void Walk(string dir)
            {
                foreach (var entry in Directory.GetFileSystemEntries(dir).OrderBy(e => e, StringComparer.Ordinal))
                {
                    var rel = Path.GetRelativePath(root, entry);
                    if (IsSymlink(entry))
                    {
                        entries.Add($"link {rel} -> {LinkTarget(entry)}");
                        continue;
                    }
                    if (Directory.Exists(entry))
                    {
                        entries.Add($"dir  {rel}");
                        Walk(entry);
                        continue;
                    }
                    entries.Add($"file {rel} = {File.ReadAllText(entry)}");
                }
            }
            Walk(root);
            return entries;
        }

        // ── targets guards (the "iOS stays shallow" proof) ─────────────────────────────────────

        [Fact]
        public void Targets_RestrictTheStepToMacAppBundlesOnAMacHost()
        {
            var targets = File.ReadAllText(TargetsPath);

            // The step is scoped to macOS / Mac Catalyst TFMs. iOS and tvOS frameworks must stay
            // shallow, so their absence from this condition is the guarantee, not an omission.
            Assert.Contains("TargetFramework.Contains('macos')", targets);
            Assert.Contains("TargetFramework.Contains('maccatalyst')", targets);
            Assert.DoesNotContain("TargetFramework.Contains('ios')", targets);
            Assert.DoesNotContain("TargetFramework.Contains('tvos')", targets);

            // Structural second guard: only a macOS/Catalyst bundle has Contents/, and that is the
            // only directory the step is ever pointed at.
            Assert.Contains("$(AppBundleDir)/Contents/Frameworks", targets);

            // The rewrite is `mv`/`ln -s` against a bundle on the build Mac.
            Assert.Contains("'$(OS)' == 'Unix'", targets);
        }

        [Fact]
        public void Targets_RunAfterPostProcessingAndBeforeEveryEntryIntoSigning()
        {
            var targets = File.ReadAllText(TargetsPath);

            // Editing a sealed bundle invalidates the seal, so the rewrite has to precede signing.
            Assert.Contains("AfterTargets=\"_PostProcessAppBundle\"", targets);
            Assert.Contains("BeforeTargets=\"_CollectCodesigningData;_CodesignAppBundle;Codesign\"", targets);
        }

        [Fact]
        public void Targets_RemoveAnAlreadyVersionedCopyBeforeTheWorkloadCopiesOverIt()
        {
            var targets = File.ReadAllText(TargetsPath);

            // The workload embeds frameworks with ditto, which cannot copy a directory onto the
            // directory links a rewritten bundle carries at its root ("Modules: Not a directory").
            // Its copy is stamp-gated, so the collision only surfaces when the stamp is stale — a
            // package update, a lost obj/, a publish whose inputs moved — which is exactly when a
            // consumer least expects a build to break. The step that clears the sentinel, which
            // already runs before the copy, therefore also removes every destination that has a
            // Versions/ tree and the workload's stamp for it, so the copy starts from nothing.
            var start = targets.IndexOf("<Target Name=\"_SwiftBindingsResetMacFrameworkAnatomyStamp\"", StringComparison.Ordinal);
            Assert.True(start >= 0);
            var end = targets.IndexOf("</Target>", start, StringComparison.Ordinal);
            var resetTarget = targets[start..end];

            Assert.Contains("BeforeTargets=\"_CopyDirectoriesToBundle\"", resetTarget);
            Assert.Contains("@(_DirectoriesToPublish)", resetTarget);
            Assert.Contains("Exists('%(_DirectoriesToPublish.TargetDirectory)/Versions')", resetTarget);

            // The stamp is what tells the workload's incremental copy the destination is current,
            // so it has to be gone before the destination is, and its removal must not be allowed
            // to fail quietly — a surviving stamp over a removed destination is a copy that gets
            // skipped and an app that ships without the framework.
            var removeDir = resetTarget.IndexOf("<RemoveDir Directories=\"%(_SwiftBindingsDeepenedFrameworkToRefresh.TargetDirectory)\"", StringComparison.Ordinal);
            var deleteStamp = resetTarget.IndexOf("<Delete Files=\"%(_SwiftBindingsDeepenedFrameworkToRefresh.StampLocation)\"", StringComparison.Ordinal);
            Assert.True(removeDir >= 0 && deleteStamp >= 0);
            Assert.True(deleteStamp < removeDir, "the workload's copy stamp must be deleted before its destination is removed");
            var deleteStampElement = resetTarget[deleteStamp..resetTarget.IndexOf("/>", deleteStamp, StringComparison.Ordinal)];
            Assert.DoesNotContain("ContinueOnError", deleteStampElement);
        }

        [Fact]
        public void Targets_OfferAnOptOutAndAPerFrameworkExclude()
        {
            var targets = File.ReadAllText(TargetsPath);

            Assert.Contains("SwiftBindingsDeepenMacFrameworks", targets);
            Assert.Contains("SwiftBindingsMacFrameworkAnatomyExclude", targets);
        }

        [Fact]
        public void Targets_FailClosedWhenTheStepDoesNotRunOnAMacAppThatEmbeddedFrameworks()
        {
            var targets = File.ReadAllText(TargetsPath);

            // A build that embedded frameworks but never rewrote them would otherwise succeed and
            // only be caught at upload.
            Assert.Contains("_SwiftBindingsVerifyMacFrameworkAnatomy", targets);
            Assert.Contains("SWIFTBIND082", targets);
        }

        [Fact]
        public void RuntimeTargets_ImportTheAnatomyStepUngatedByTheNativeSwitch()
        {
            var runtimeTargets = File.ReadAllText(Path.Combine(BuildDir, "SwiftBindings.Runtime.targets"));

            var start = runtimeTargets.IndexOf("<Import Project=", StringComparison.Ordinal);
            Assert.True(start >= 0, "SwiftBindings.Runtime.targets must import the anatomy step.");
            var end = runtimeTargets.IndexOf("/>", start, StringComparison.Ordinal);
            Assert.True(end > start, "The anatomy import element is not terminated.");
            var importElement = runtimeTargets[start..end];

            Assert.Contains("SwiftBindings.MacFrameworkAnatomy.targets", importElement);

            // Whether this package contributes its own native framework is a separate question from
            // whether the bundle's frameworks need the right shape, so the import must not inherit
            // that switch as a condition.
            Assert.DoesNotContain("IncludeSwiftBindingsRuntimeNative", importElement);
        }

        [Fact]
        public void Packaging_ShipsTheStepAlongsideTheConsumerTargets()
        {
            var repoRoot = FindRepoRoot();

            var runtimeCsproj = File.ReadAllText(
                Path.Combine(repoRoot, "src", "Swift.Runtime", "src", "Swift.Runtime.csproj"));
            Assert.Contains("build/SwiftBindings.MacFrameworkAnatomy.targets", runtimeCsproj);
            Assert.Contains("build/deepen-mac-framework.sh", runtimeCsproj);

            // The SDK carries a copy for apps that opt out of the implicit runtime reference.
            var sdkCsproj = File.ReadAllText(
                Path.Combine(repoRoot, "src", "Swift.Bindings.Sdk", "Swift.Bindings.Sdk.csproj"));
            Assert.Contains("SwiftBindings.MacFrameworkAnatomy.targets", sdkCsproj);
            Assert.Contains("deepen-mac-framework.sh", sdkCsproj);
        }
    }
}
