// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Slices a source xcframework down to only the slices a given NuGet RID can consume,
    /// staging the result at a destination directory with a pruned root <c>Info.plist</c>.
    ///
    /// Used by both the SDK pack pipeline and the standalone-CLI generator path so that
    /// per-RID <c>runtimes/&lt;rid&gt;/native/&lt;Name&gt;.xcframework/</c> directories no
    /// longer contain unrelated platform slices (e.g. watchOS slices under <c>ios-arm64</c>).
    ///
    /// Slice copies use <c>ditto</c> to preserve symlinks, extended attributes, executable
    /// bits, and per-framework <c>_CodeSignature/</c> directories — required for valid
    /// macOS framework bundles.
    /// </summary>
    public static class XCFrameworkSlicer
    {
        /// <summary>
        /// NuGet RIDs the slicer recognizes. Mapping to <c>SupportedPlatform</c> +
        /// <c>SupportedPlatformVariant</c> matches the table in the
        /// <c>per-rid-xcframework-slicing.md</c> design doc. The x86_64 RIDs cover the
        /// Intel Apple targets (osx-x64 desktop, Mac Catalyst, iOS/tvOS x86_64 simulators);
        /// there is no x86_64 iOS/tvOS *device*, so those Intel RIDs are simulator-only.
        /// </summary>
        public static readonly IReadOnlyList<string> SupportedRids = new[]
        {
            "ios-arm64",
            "tvos-arm64",
            "osx-arm64",
            "maccatalyst-arm64",
            "osx-x64",
            "maccatalyst-x64",
            "iossimulator-x64",
            "tvossimulator-x64",
        };

        /// <summary>
        /// Stages a sliced copy of <paramref name="sourceXcfwPath"/> at
        /// <paramref name="destPath"/> containing only slice directories the given
        /// <paramref name="nuGetRid"/> can consume. Writes a pruned XML <c>Info.plist</c>
        /// listing only the retained slices. Throws on zero-slice match.
        /// </summary>
        /// <param name="sourceXcfwPath">Absolute path to the source xcframework directory.</param>
        /// <param name="nuGetRid">NuGet RID, e.g. <c>ios-arm64</c>, <c>osx-arm64</c>.</param>
        /// <param name="destPath">Destination directory. Created if missing; emptied if present.</param>
        /// <param name="logger">Logger for progress diagnostics.</param>
        /// <param name="runner">Command runner for invoking <c>ditto</c>; defaults to <see cref="SystemCommandRunner"/>.</param>
        public static void Slice(
            string sourceXcfwPath,
            string nuGetRid,
            string destPath,
            ILogger logger,
            ICommandRunner? runner = null)
        {
            if (string.IsNullOrEmpty(sourceXcfwPath))
                throw new ArgumentException("Source xcframework path is required.", nameof(sourceXcfwPath));
            if (string.IsNullOrEmpty(nuGetRid))
                throw new ArgumentException("NuGet RID is required.", nameof(nuGetRid));
            if (string.IsNullOrEmpty(destPath))
                throw new ArgumentException("Destination path is required.", nameof(destPath));
            if (!Directory.Exists(sourceXcfwPath))
                throw new DirectoryNotFoundException($"Source xcframework not found: '{sourceXcfwPath}'");

            sourceXcfwPath = Path.GetFullPath(sourceXcfwPath);
            destPath = Path.GetFullPath(destPath);

            runner ??= new SystemCommandRunner();

            var infoPlistPath = Path.Combine(sourceXcfwPath, "Info.plist");
            if (!File.Exists(infoPlistPath))
                throw new FileNotFoundException($"Info.plist not found in source xcframework: '{infoPlistPath}'");

            var rootDict = PlistReader.ReadPlistDict(infoPlistPath, runner, logger)
                ?? throw new InvalidOperationException(
                    $"Failed to read xcframework Info.plist at '{infoPlistPath}'.");

            var allSlices = XCFrameworkResolver.ParseAvailableLibraries(rootDict);
            var keptSlices = allSlices.Where(s => MatchesRid(s, nuGetRid)).ToList();

            if (keptSlices.Count == 0)
            {
                static string Describe(XCFrameworkSlice s) =>
                    (s.SupportedPlatformVariant is null
                        ? s.SupportedPlatform
                        : $"{s.SupportedPlatform}/{s.SupportedPlatformVariant}")
                    + $" [{string.Join("+", s.SupportedArchitectures)}]";
                var available = string.Join(", ", allSlices.Select(Describe));

                // Distinguish "no platform match at all" from "the platform matched but the
                // slice lacks the RID's CPU arch" so an x86_64 RID against an arm64-only
                // slice fails loud with a pointed message instead of the generic no-match one.
                var requiredArch = RequiredArchitecture(nuGetRid);
                var platformMatchesArchMissing = allSlices
                    .Where(s => MatchesPlatform(s, nuGetRid))
                    .ToList();
                if (platformMatchesArchMissing.Count > 0)
                {
                    var offenders = string.Join(", ", platformMatchesArchMissing.Select(Describe));
                    throw new InvalidOperationException(
                        $"SWIFTBIND051: xcframework '{Path.GetFileName(sourceXcfwPath)}' has slice(s) for NuGet " +
                        $"RID '{nuGetRid}' but none contain the required '{requiredArch}' architecture — refusing " +
                        $"to silently fall back to another arch. Platform-compatible slice(s): [{offenders}]. " +
                        $"The source library must ship an '{requiredArch}' slice for this platform. " +
                        $"Source: '{sourceXcfwPath}'.");
                }

                throw new InvalidOperationException(
                    $"SWIFTBIND050: xcframework '{Path.GetFileName(sourceXcfwPath)}' contains no slices " +
                    $"compatible with NuGet RID '{nuGetRid}'. Available slices: [{available}]. " +
                    $"Source: '{sourceXcfwPath}'.");
            }

            PrepareDestination(destPath);

            foreach (var slice in keptSlices)
            {
                if (string.IsNullOrEmpty(slice.LibraryIdentifier))
                    throw new InvalidOperationException(
                        $"SWIFTBIND050: xcframework '{sourceXcfwPath}' has a slice with no LibraryIdentifier; cannot copy.");

                var srcSliceDir = Path.Combine(sourceXcfwPath, slice.LibraryIdentifier);
                var dstSliceDir = Path.Combine(destPath, slice.LibraryIdentifier);

                if (!Directory.Exists(srcSliceDir))
                    throw new DirectoryNotFoundException(
                        $"SWIFTBIND050: slice directory missing on disk: '{srcSliceDir}' (referenced by Info.plist).");

                CopyWithDitto(srcSliceDir, dstSliceDir, runner, logger);
            }

            WritePrunedInfoPlist(rootDict, keptSlices, Path.Combine(destPath, "Info.plist"));

            logger.LogInformation(
                "Sliced xcframework '{Name}' for RID '{Rid}': {Kept}/{Total} slices retained.",
                Path.GetFileName(sourceXcfwPath), nuGetRid, keptSlices.Count, allSlices.Count);
        }

        /// <summary>
        /// Returns true if a slice should be retained for the given NuGet RID — i.e. the
        /// slice's platform/variant is consumable by the RID (<see cref="MatchesPlatform"/>)
        /// AND the slice's fat binary actually contains the RID's CPU architecture
        /// (<see cref="RequiredArchitecture"/>). The architecture half is what makes the
        /// x86_64 RIDs fail loud instead of silently shipping an arm64-only slice: a
        /// macOS slice that carries only <c>arm64</c> matches <c>osx-x64</c> on platform
        /// but not on architecture, so it is declined here and reported by <see cref="Slice"/>.
        /// </summary>
        internal static bool MatchesRid(XCFrameworkSlice slice, string nuGetRid)
        {
            return MatchesPlatform(slice, nuGetRid) && SliceHasArchitecture(slice, nuGetRid);
        }

        /// <summary>
        /// Platform/variant half of <see cref="MatchesRid"/> — does NOT consult the slice's
        /// architectures. Used by <see cref="Slice"/> to tell "no platform match at all"
        /// apart from "platform matched but the requested CPU arch is absent".
        /// Predicate table (arch suffix stripped to a platform token):
        ///   ios            → SupportedPlatform=ios   AND (variant empty OR variant=simulator) AND variant != maccatalyst
        ///   tvos           → SupportedPlatform=tvos  AND (variant empty OR variant=simulator)
        ///   osx            → SupportedPlatform=macos AND variant empty (device only)
        ///   maccatalyst    → SupportedPlatform=ios   AND variant=maccatalyst
        ///   iossimulator   → SupportedPlatform=ios   AND variant=simulator (x86_64 has no device)
        ///   tvossimulator  → SupportedPlatform=tvos  AND variant=simulator
        /// </summary>
        internal static bool MatchesPlatform(XCFrameworkSlice slice, string nuGetRid)
        {
            var platform = slice.SupportedPlatform ?? "";
            var variant = slice.SupportedPlatformVariant; // may be null
            bool variantEmpty = string.IsNullOrEmpty(variant);
            bool variantIs(string v) => string.Equals(variant, v, StringComparison.OrdinalIgnoreCase);
            bool platformIs(string p) => string.Equals(platform, p, StringComparison.OrdinalIgnoreCase);

            switch (PlatformToken(nuGetRid))
            {
                case "ios":
                    return platformIs("ios") && (variantEmpty || variantIs("simulator")) && !variantIs("maccatalyst");
                case "tvos":
                    return platformIs("tvos") && (variantEmpty || variantIs("simulator"));
                case "osx":
                    return platformIs("macos") && variantEmpty;
                case "maccatalyst":
                    return platformIs("ios") && variantIs("maccatalyst");
                case "iossimulator":
                    return platformIs("ios") && variantIs("simulator");
                case "tvossimulator":
                    return platformIs("tvos") && variantIs("simulator");
                default:
                    throw new ArgumentException(
                        $"SWIFTBIND050: unrecognized NuGet RID '{nuGetRid}'. Supported: " +
                        string.Join(", ", SupportedRids), nameof(nuGetRid));
            }
        }

        /// <summary>
        /// The Mach-O architecture name (<c>arm64</c> or <c>x86_64</c>) a RID requires its
        /// slice to contain. The RID arch suffix is <c>-arm64</c> or <c>-x64</c>; the latter
        /// maps to Apple's <c>x86_64</c> slice-architecture spelling.
        /// </summary>
        internal static string RequiredArchitecture(string nuGetRid)
        {
            if (nuGetRid.EndsWith("-x64", StringComparison.Ordinal))
                return "x86_64";
            if (nuGetRid.EndsWith("-arm64", StringComparison.Ordinal))
                return "arm64";
            throw new ArgumentException(
                $"SWIFTBIND050: NuGet RID '{nuGetRid}' has no recognized architecture suffix " +
                $"('-arm64' or '-x64'). Supported: " + string.Join(", ", SupportedRids), nameof(nuGetRid));
        }

        private static bool SliceHasArchitecture(XCFrameworkSlice slice, string nuGetRid) =>
            slice.SupportedArchitectures.Any(a =>
                string.Equals(a, RequiredArchitecture(nuGetRid), StringComparison.OrdinalIgnoreCase));

        /// <summary>Strips the <c>-arm64</c>/<c>-x64</c> suffix, leaving the platform token.</summary>
        private static string PlatformToken(string nuGetRid)
        {
            if (nuGetRid.EndsWith("-x64", StringComparison.Ordinal))
                return nuGetRid.Substring(0, nuGetRid.Length - "-x64".Length);
            if (nuGetRid.EndsWith("-arm64", StringComparison.Ordinal))
                return nuGetRid.Substring(0, nuGetRid.Length - "-arm64".Length);
            return nuGetRid; // falls through to the unrecognized-RID throw in MatchesPlatform
        }

        private static void PrepareDestination(string destPath)
        {
            if (Directory.Exists(destPath))
            {
                // If destPath itself is a symlink to a directory, EnumerateFileSystemEntries
                // would follow it and wipe the link target's contents. Delete the symlink
                // and recreate destPath as a real directory instead.
                var rootAttrs = File.GetAttributes(destPath);
                if ((rootAttrs & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(destPath); // unlinks the symlink itself, not the target
                    Directory.CreateDirectory(destPath);
                    return;
                }

                // Empty stale contents so re-slicing produces a clean tree.
                foreach (var entry in Directory.EnumerateFileSystemEntries(destPath))
                {
                    var attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.Directory) != 0 && (attrs & FileAttributes.ReparsePoint) == 0)
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }
            }
            else
            {
                Directory.CreateDirectory(destPath);
            }
        }

        private static void CopyWithDitto(string src, string dst, ICommandRunner runner, ILogger logger)
        {
            // ditto preserves symlinks, xattrs, executable bits, and per-slice _CodeSignature/.
            // It overwrites the destination, so we don't pre-create it.
            var args = $"\"{src}\" \"{dst}\"";
            var (exitCode, stdout, stderr) = runner.Run("ditto", args, timeoutMs: 120_000);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"SWIFTBIND050: ditto failed copying slice '{src}' -> '{dst}' " +
                    $"(exit {exitCode}). stderr: {stderr}");
            }
            logger.LogDebug("ditto staged slice '{Src}' -> '{Dst}'", src, dst);
        }

        /// <summary>
        /// Writes a fresh xcframework <c>Info.plist</c> whose <c>AvailableLibraries</c> array is
        /// rebuilt from <paramref name="keptSlices"/> (preserving every other root key from
        /// <paramref name="rootDict"/>). Shared with <see cref="WrapperXCFrameworkMerger"/> so a
        /// lipo-merged wrapper can rewrite its plist with unioned <c>SupportedArchitectures</c>.
        /// </summary>
        internal static void WritePrunedInfoPlist(
            Dictionary<string, object> rootDict,
            List<XCFrameworkSlice> keptSlices,
            string destPlistPath)
        {
            var doc = new XmlDocument();
            var docType = doc.CreateDocumentType("plist",
                "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd",
                null);
            doc.AppendChild(doc.CreateXmlDeclaration("1.0", "UTF-8", null));
            doc.AppendChild(docType);

            var plist = doc.CreateElement("plist");
            plist.SetAttribute("version", "1.0");
            doc.AppendChild(plist);

            var rootDictNode = doc.CreateElement("dict");
            plist.AppendChild(rootDictNode);

            // Preserve all root-level keys except AvailableLibraries, which we rewrite below.
            // Most xcframeworks have only AvailableLibraries + CFBundlePackageType + XCFrameworkFormatVersion.
            foreach (var (key, value) in rootDict)
            {
                if (string.Equals(key, "AvailableLibraries", StringComparison.Ordinal))
                    continue;
                AppendKey(doc, rootDictNode, key);
                AppendValue(doc, rootDictNode, value);
            }

            // AvailableLibraries — rewritten to only the kept slices.
            AppendKey(doc, rootDictNode, "AvailableLibraries");
            var arrayNode = doc.CreateElement("array");
            rootDictNode.AppendChild(arrayNode);
            foreach (var slice in keptSlices)
                arrayNode.AppendChild(BuildSliceDict(doc, slice));

            using var writer = XmlWriter.Create(destPlistPath, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "\t",
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            });
            doc.Save(writer);
        }

        private static XmlElement BuildSliceDict(XmlDocument doc, XCFrameworkSlice slice)
        {
            var dict = doc.CreateElement("dict");
            if (!string.IsNullOrEmpty(slice.BinaryPath))
            {
                AppendKey(doc, dict, "BinaryPath");
                AppendString(doc, dict, slice.BinaryPath);
            }
            AppendKey(doc, dict, "LibraryIdentifier");
            AppendString(doc, dict, slice.LibraryIdentifier);
            AppendKey(doc, dict, "LibraryPath");
            AppendString(doc, dict, slice.LibraryPath);
            AppendKey(doc, dict, "SupportedArchitectures");
            var archArray = doc.CreateElement("array");
            foreach (var arch in slice.SupportedArchitectures)
            {
                var s = doc.CreateElement("string");
                s.InnerText = arch;
                archArray.AppendChild(s);
            }
            dict.AppendChild(archArray);
            AppendKey(doc, dict, "SupportedPlatform");
            AppendString(doc, dict, slice.SupportedPlatform);
            if (!string.IsNullOrEmpty(slice.SupportedPlatformVariant))
            {
                AppendKey(doc, dict, "SupportedPlatformVariant");
                AppendString(doc, dict, slice.SupportedPlatformVariant!);
            }
            return dict;
        }

        private static void AppendKey(XmlDocument doc, XmlElement parent, string key)
        {
            var k = doc.CreateElement("key");
            k.InnerText = key;
            parent.AppendChild(k);
        }

        private static void AppendString(XmlDocument doc, XmlElement parent, string value)
        {
            var s = doc.CreateElement("string");
            s.InnerText = value;
            parent.AppendChild(s);
        }

        private static void AppendValue(XmlDocument doc, XmlElement parent, object value)
        {
            switch (value)
            {
                case string s:
                    AppendString(doc, parent, s);
                    break;
                case bool b:
                    parent.AppendChild(doc.CreateElement(b ? "true" : "false"));
                    break;
                case int i:
                    var intEl = doc.CreateElement("integer");
                    intEl.InnerText = i.ToString();
                    parent.AppendChild(intEl);
                    break;
                case List<object> arr:
                    var arrEl = doc.CreateElement("array");
                    foreach (var v in arr)
                        AppendValue(doc, arrEl, v);
                    parent.AppendChild(arrEl);
                    break;
                case Dictionary<string, object> nested:
                    var dictEl = doc.CreateElement("dict");
                    foreach (var (k, v) in nested)
                    {
                        AppendKey(doc, dictEl, k);
                        AppendValue(doc, dictEl, v);
                    }
                    parent.AppendChild(dictEl);
                    break;
                default:
                    AppendString(doc, parent, value?.ToString() ?? "");
                    break;
            }
        }
    }
}
