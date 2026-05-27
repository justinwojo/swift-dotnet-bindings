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
        /// Merges <paramref name="secondaryXcfwPath"/> into <paramref name="primaryXcfwPath"/>
        /// in place, then deletes the secondary. Slices present in only one input are kept as-is
        /// (e.g. the arm64-only device slice has no x86_64 counterpart — there is no Intel device).
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

            var mergedSlices = new List<XCFrameworkSlice>();
            foreach (var p in primarySlices)
            {
                var match = secondarySlices.FirstOrDefault(s =>
                    string.Equals(s.LibraryIdentifier, p.LibraryIdentifier, StringComparison.Ordinal));
                if (match == null)
                {
                    // Slice only in primary (e.g. the arm64 device slice the x86_64 pass skipped —
                    // no Intel device target). Keep its single-arch binary unchanged.
                    mergedSlices.Add(p);
                    continue;
                }

                var primaryBin = Path.Combine(primaryXcfwPath, p.LibraryIdentifier, ResolveBinaryRelPath(p));
                var secondaryBin = Path.Combine(secondaryXcfwPath, match.LibraryIdentifier, ResolveBinaryRelPath(match));
                if (!File.Exists(primaryBin))
                    throw new FileNotFoundException($"SWIFTBIND053: primary slice binary not found: '{primaryBin}'.");
                if (!File.Exists(secondaryBin))
                    throw new FileNotFoundException($"SWIFTBIND053: secondary slice binary not found: '{secondaryBin}'.");

                LipoCreate(primaryBin, secondaryBin, runner, logger);

                var unionArchs = p.SupportedArchitectures
                    .Concat(match.SupportedArchitectures)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                mergedSlices.Add(CloneWithArchitectures(p, unionArchs));
                logger.LogInformation("Fattened wrapper slice '{Slice}' -> [{Archs}]",
                    p.LibraryIdentifier, string.Join("+", unionArchs));
            }

            // Slices present ONLY in the secondary would mean the primary pass missed a platform —
            // unexpected for the arm64-primary flow, but fold them in rather than silently drop
            // coverage.
            foreach (var s in secondarySlices)
            {
                if (primarySlices.Any(p => string.Equals(p.LibraryIdentifier, s.LibraryIdentifier, StringComparison.Ordinal)))
                    continue;
                var srcSliceDir = Path.Combine(secondaryXcfwPath, s.LibraryIdentifier);
                var dstSliceDir = Path.Combine(primaryXcfwPath, s.LibraryIdentifier);
                var (exit, _, stderr) = runner.Run("ditto", $"\"{srcSliceDir}\" \"{dstSliceDir}\"", timeoutMs: 120_000);
                if (exit != 0)
                    throw new InvalidOperationException(
                        $"SWIFTBIND053: ditto failed copying secondary-only slice '{srcSliceDir}' (exit {exit}): {stderr}");
                mergedSlices.Add(s);
                logger.LogWarning("Wrapper slice '{Slice}' was present only in the secondary-arch xcframework; copied into the merged result.",
                    s.LibraryIdentifier);
            }

            XCFrameworkSlicer.WritePrunedInfoPlist(primaryRoot, mergedSlices, primaryPlist);

            try { Directory.Delete(secondaryXcfwPath, true); }
            catch { /* best-effort: the merged primary is what callers consume */ }
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
