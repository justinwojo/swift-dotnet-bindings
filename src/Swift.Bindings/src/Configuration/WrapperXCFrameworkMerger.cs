// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Folds a secondary single-architecture wrapper xcframework into a primary one by
    /// <c>lipo</c>-ing each shared slice's framework binary into a fat binary and rewriting the
    /// primary's <c>Info.plist</c> <c>SupportedArchitectures</c> to the union.
    ///
    /// This is what lets one multi-arch wrapper xcframework serve BOTH Apple Silicon and Intel
    /// (Rosetta) consumers from a single <c>runtimes/&lt;rid&gt;/native/</c> tree: the generator
    /// compiles the wrapper once per CPU arch (arm64 primary, x86_64 secondary) and merges the
    /// results here, rather than duplicating per-RID directory trees. .NET-for-Apple's
    /// <c>ResolveNativeReferences</c> then selects the matching slice from the fat binary by the
    /// consumer's target architecture, exactly as it does for any Apple multi-arch xcframework.
    /// </summary>
    public static class WrapperXCFrameworkMerger
    {
        /// <summary>
        /// Merges <paramref name="secondaryXcfwPath"/> into <paramref name="primaryXcfwPath"/>,
        /// then deletes the secondary. Slices present in only one input are kept as-is
        /// (e.g. the arm64-only device slice has no x86_64 counterpart — there is no Intel device).
        ///
        /// The fold is transactional: every lipo/ditto/plist mutation is applied to a staging COPY
        /// of the primary xcframework, and the result is swapped into place with a single directory
        /// rename only after the whole merge succeeds. A failure mid-merge therefore leaves the
        /// primary xcframework byte-for-byte intact — never a fat binary advertised by a stale
        /// single-arch <c>Info.plist</c> (the resolver keys slice selection on the plist, so that
        /// desync would deny the folded arch and surface as a DllNotFound for Rosetta/x64 consumers).
        /// </summary>
        public static void MergeFatSlices(
            string primaryXcfwPath,
            string secondaryXcfwPath,
            ILogger logger,
            ICommandRunner? runner = null)
        {
            runner ??= new SystemCommandRunner();
            primaryXcfwPath = Path.GetFullPath(primaryXcfwPath);
            secondaryXcfwPath = Path.GetFullPath(secondaryXcfwPath);

            // Recover from an interrupted prior commit. The commit phase below renames the live
            // primary to '<primary>.superseded' and then moves staging into its place; a hard kill
            // (process killed, reboot, power loss) BETWEEN those two renames leaves the original
            // intact in '.superseded' but absent at the primary path. The in-process catch only
            // restores within the same run, so heal it here on the next run: if the primary is
            // missing, move the superseded tree back; otherwise the primary won the swap and the
            // leftover superseded is just stale residue to clear before we rename onto that name.
            var supersededPath = primaryXcfwPath + ".superseded";
            if (Directory.Exists(supersededPath))
            {
                if (!Directory.Exists(primaryXcfwPath))
                {
                    // Same-volume directory rename (siblings under one parent) is a single atomic
                    // rename(2): the original is recovered whole, never a torn tree.
                    logger.LogWarning(
                        "Recovering wrapper xcframework '{Primary}' from an interrupted prior merge ('{Superseded}' left behind); re-running the fat fold.",
                        primaryXcfwPath, supersededPath);
                    Directory.Move(supersededPath, primaryXcfwPath);
                }
                else
                {
                    Directory.Delete(supersededPath, true);
                }
            }

            var primaryPlist = Path.Combine(primaryXcfwPath, "Info.plist");
            var secondaryPlist = Path.Combine(secondaryXcfwPath, "Info.plist");
            if (!File.Exists(primaryPlist))
                throw new FileNotFoundException($"SWIFTBIND053: primary wrapper Info.plist not found: '{primaryPlist}'.");
            if (!File.Exists(secondaryPlist))
                throw new FileNotFoundException($"SWIFTBIND053: secondary wrapper Info.plist not found: '{secondaryPlist}'.");

            var primaryRoot = PlistReader.ReadPlistDict(primaryPlist, runner, logger)
                ?? throw new InvalidOperationException($"SWIFTBIND053: failed to read '{primaryPlist}'.");
            var secondaryRoot = PlistReader.ReadPlistDict(secondaryPlist, runner, logger)
                ?? throw new InvalidOperationException($"SWIFTBIND053: failed to read '{secondaryPlist}'.");

            var primarySlices = XCFrameworkResolver.ParseAvailableLibraries(primaryRoot);
            var secondarySlices = XCFrameworkResolver.ParseAvailableLibraries(secondaryRoot);

            // Build the merged result in a staging copy of the primary tree (siblings → same volume,
            // so the final swap is a rename). Generator-produced wrapper/bridge frameworks are flat
            // bundles (no Versions/ symlinks on any platform), so a plain recursive copy is faithful.
            var stagingPath = primaryXcfwPath + ".merge-staging";
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, true);
            CopyDirectory(primaryXcfwPath, stagingPath);

            try
            {
                var stagingPlist = Path.Combine(stagingPath, "Info.plist");

                // Match by *semantic identity* — (SupportedPlatform, SupportedPlatformVariant) — not by
                // LibraryIdentifier. SliceVariant.WithArchitecture renames the slice ID to embed the
                // active arch (e.g. "ios-arm64-simulator" → "ios-x86_64-simulator"), so a primary arm64
                // pass and a secondary x86_64 pass on the SAME sim slice land in differently-named
                // directories. The correct fold is still one fat sim slice with both archs — we lipo the
                // two binaries and keep the primary's LibraryIdentifier (and on-disk directory) intact,
                // so the resulting xcframework presents a single fat slice with SupportedArchitectures
                // = [arm64, x86_64]. .NET-for-Apple's NativeReference resolver requires this: it asks for
                // a slice whose (platform, variant, arch) match and rejects an xcframework that exposes
                // two separate per-arch sim slices.
                var mergedSlices = new List<XCFrameworkSlice>();
                var consumedSecondaryIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in primarySlices)
                {
                    var match = secondarySlices.FirstOrDefault(s =>
                        !consumedSecondaryIds.Contains(s.LibraryIdentifier) &&
                        string.Equals(s.SupportedPlatform, p.SupportedPlatform, StringComparison.Ordinal) &&
                        string.Equals(s.SupportedPlatformVariant, p.SupportedPlatformVariant, StringComparison.Ordinal));
                    if (match == null)
                    {
                        // Slice only in primary (e.g. the arm64 device slice the x86_64 pass skipped —
                        // no Intel device target). Keep its single-arch binary unchanged.
                        mergedSlices.Add(p);
                        continue;
                    }

                    // lipo the STAGING copy of the primary binary against the secondary's binary.
                    var stagingBin = Path.Combine(stagingPath, p.LibraryIdentifier, ResolveBinaryRelPath(p));
                    var secondaryBin = Path.Combine(secondaryXcfwPath, match.LibraryIdentifier, ResolveBinaryRelPath(match));
                    if (!File.Exists(stagingBin))
                        throw new FileNotFoundException($"SWIFTBIND053: primary slice binary not found: '{stagingBin}'.");
                    if (!File.Exists(secondaryBin))
                        throw new FileNotFoundException($"SWIFTBIND053: secondary slice binary not found: '{secondaryBin}'.");

                    LipoCreate(stagingBin, secondaryBin, runner, logger);
                    consumedSecondaryIds.Add(match.LibraryIdentifier);

                    var unionArchs = p.SupportedArchitectures
                        .Concat(match.SupportedArchitectures)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    mergedSlices.Add(CloneWithArchitectures(p, unionArchs));
                    logger.LogInformation(
                        "Fattened wrapper slice '{Primary}' (folded secondary '{Secondary}') -> [{Archs}]",
                        p.LibraryIdentifier, match.LibraryIdentifier, string.Join("+", unionArchs));
                }

                // Slices present ONLY in the secondary (no semantic match in primary) — fold them in.
                // Unexpected for the arm64-primary flow (the primary covers every requested slice), but
                // keep the safety net so a missed platform is preserved rather than silently dropped.
                foreach (var s in secondarySlices)
                {
                    if (consumedSecondaryIds.Contains(s.LibraryIdentifier))
                        continue;
                    var srcSliceDir = Path.Combine(secondaryXcfwPath, s.LibraryIdentifier);
                    var dstSliceDir = Path.Combine(stagingPath, s.LibraryIdentifier);
                    var (exit, _, stderr) = runner.Run("ditto", $"\"{srcSliceDir}\" \"{dstSliceDir}\"", timeoutMs: 120_000);
                    if (exit != 0)
                        throw new InvalidOperationException(
                            $"SWIFTBIND053: ditto failed copying secondary-only slice '{srcSliceDir}' (exit {exit}): {stderr}");
                    mergedSlices.Add(s);
                    logger.LogWarning("Wrapper slice '{Slice}' was present only in the secondary-arch xcframework; copied into the merged result.",
                        s.LibraryIdentifier);
                }

                XCFrameworkSlicer.WritePrunedInfoPlist(primaryRoot, mergedSlices, stagingPlist);
            }
            catch
            {
                // The primary tree was never touched; discard the half-built staging copy and rethrow.
                try { Directory.Delete(stagingPath, true); } catch { /* best-effort cleanup */ }
                throw;
            }

            // Commit: rename the live primary aside, move staging into its place, then drop the old
            // tree. If the second rename fails, restore the original so the primary is never left
            // missing (and the start-of-method recovery heals a hard-kill between the two renames on
            // the next run). supersededPath was cleared of any stale residue above.
            Directory.Move(primaryXcfwPath, supersededPath);
            try
            {
                Directory.Move(stagingPath, primaryXcfwPath);
            }
            catch
            {
                if (!Directory.Exists(primaryXcfwPath) && Directory.Exists(supersededPath))
                    Directory.Move(supersededPath, primaryXcfwPath);
                throw;
            }
            try { Directory.Delete(supersededPath, true); } catch { /* best-effort cleanup */ }

            try { Directory.Delete(secondaryXcfwPath, true); }
            catch { /* best-effort: the merged primary is what callers consume */ }
        }

        /// <summary>
        /// Recursively copies <paramref name="sourceDir"/> into <paramref name="destDir"/>. Used to
        /// stage the primary xcframework before an in-staging merge. Generator-emitted wrapper/bridge
        /// frameworks are flat bundles (binary + Info.plist + Modules/), so files and nested
        /// directories are all that need replicating.
        /// </summary>
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }

        private static string ResolveBinaryRelPath(XCFrameworkSlice slice)
        {
            if (!string.IsNullOrEmpty(slice.BinaryPath))
                return slice.BinaryPath;
            if (slice.LibraryPath.EndsWith(".framework", StringComparison.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(slice.LibraryPath);
                return Path.Combine(slice.LibraryPath, name);
            }
            return slice.LibraryPath;
        }

        private static XCFrameworkSlice CloneWithArchitectures(XCFrameworkSlice slice, List<string> architectures) =>
            new XCFrameworkSlice
            {
                BinaryPath = slice.BinaryPath,
                LibraryIdentifier = slice.LibraryIdentifier,
                LibraryPath = slice.LibraryPath,
                SupportedArchitectures = architectures,
                SupportedPlatform = slice.SupportedPlatform,
                SupportedPlatformVariant = slice.SupportedPlatformVariant,
            };

        private static void LipoCreate(string primaryBin, string secondaryBin, ICommandRunner runner, ILogger logger)
        {
            // lipo -create writes a fat Mach-O; emit to a temp then move over the primary binary.
            var tmp = primaryBin + ".fat";
            var args = $"lipo -create \"{primaryBin}\" \"{secondaryBin}\" -output \"{tmp}\"";
            var (exit, _, stderr) = runner.Run("xcrun", args, timeoutMs: 60_000);
            if (exit != 0)
                throw new InvalidOperationException(
                    $"SWIFTBIND053: lipo -create failed merging '{secondaryBin}' into '{primaryBin}' (exit {exit}): {stderr}");
            File.Delete(primaryBin);
            File.Move(tmp, primaryBin);
            logger.LogDebug("lipo merged '{Secondary}' into '{Primary}'", secondaryBin, primaryBin);
        }
    }
}
