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
        /// <c>per-rid-xcframework-slicing.md</c> design doc.
        /// </summary>
        public static readonly IReadOnlyList<string> SupportedRids = new[]
        {
            "ios-arm64",
            "tvos-arm64",
            "osx-arm64",
            "maccatalyst-arm64",
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
                var available = string.Join(", ", allSlices.Select(s =>
                    s.SupportedPlatformVariant is null
                        ? s.SupportedPlatform
                        : $"{s.SupportedPlatform}/{s.SupportedPlatformVariant}"));
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
        /// Returns true if a slice should be retained for the given NuGet RID.
        /// Predicate table:
        ///   ios-arm64        → SupportedPlatform=ios   AND (variant empty OR variant=simulator)  AND variant != maccatalyst
        ///   tvos-arm64       → SupportedPlatform=tvos  AND (variant empty OR variant=simulator)
        ///   osx-arm64        → SupportedPlatform=macos AND variant empty (device only)
        ///   maccatalyst-arm64 → SupportedPlatform=ios  AND variant=maccatalyst
        /// </summary>
        internal static bool MatchesRid(XCFrameworkSlice slice, string nuGetRid)
        {
            var platform = slice.SupportedPlatform ?? "";
            var variant = slice.SupportedPlatformVariant; // may be null
            bool variantEmpty = string.IsNullOrEmpty(variant);
            bool variantIs(string v) => string.Equals(variant, v, StringComparison.OrdinalIgnoreCase);
            bool platformIs(string p) => string.Equals(platform, p, StringComparison.OrdinalIgnoreCase);

            switch (nuGetRid)
            {
                case "ios-arm64":
                    return platformIs("ios") && (variantEmpty || variantIs("simulator")) && !variantIs("maccatalyst");
                case "tvos-arm64":
                    return platformIs("tvos") && (variantEmpty || variantIs("simulator"));
                case "osx-arm64":
                    return platformIs("macos") && variantEmpty;
                case "maccatalyst-arm64":
                    return platformIs("ios") && variantIs("maccatalyst");
                default:
                    throw new ArgumentException(
                        $"SWIFTBIND050: unrecognized NuGet RID '{nuGetRid}'. Supported: " +
                        string.Join(", ", SupportedRids), nameof(nuGetRid));
            }
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

        private static void WritePrunedInfoPlist(
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
