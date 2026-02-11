// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Xml;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Shared reader for Apple property list files. Handles both binary and XML plists.
    /// Binary plists (used by inner framework Info.plists) are converted via plutil;
    /// XML plists (used by self-generated wrapper plists) are read directly.
    /// </summary>
    public static class PlistReader
    {
        /// <summary>
        /// Reads a plist file and returns its root dictionary.
        /// First tries plutil conversion (handles binary + XML plists).
        /// Falls back to direct XmlDocument.Load (XML plists only).
        /// Returns null on total failure.
        /// </summary>
        public static Dictionary<string, object>? ReadPlistDict(
            string plistPath,
            ICommandRunner? commandRunner,
            ILogger logger)
        {
            if (!File.Exists(plistPath))
            {
                logger.LogDebug("Plist file not found: {Path}", plistPath);
                return null;
            }

            // Try plutil first (handles binary plists)
            var plutilResult = TryReadViaPlutil(plistPath, commandRunner ?? new SystemCommandRunner(), logger);
            if (plutilResult != null)
                return plutilResult;

            // Fallback: direct XML load (only works for XML plists)
            return TryReadDirectXml(plistPath, logger);
        }

        private static Dictionary<string, object>? TryReadViaPlutil(
            string plistPath, ICommandRunner commandRunner, ILogger logger)
        {
            try
            {
                var (exitCode, stdout, stderr) = commandRunner.Run(
                    "plutil",
                    $"-convert xml1 -o /dev/stdout \"{plistPath}\"",
                    timeoutMs: 10000);

                if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    logger.LogDebug("plutil conversion failed (exit {Code}): {Error}", exitCode, stderr);
                    return null;
                }

                return ParseXmlPlistString(stdout);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "plutil invocation failed for {Path}", plistPath);
                return null;
            }
        }

        private static Dictionary<string, object>? TryReadDirectXml(string plistPath, ILogger logger)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(plistPath);

                var rootDict = doc.SelectSingleNode("/plist/dict");
                if (rootDict == null)
                {
                    logger.LogDebug("No root dict in plist: {Path}", plistPath);
                    return null;
                }

                return XCFrameworkResolver.ParsePlistDict(rootDict);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Direct XML plist read failed for {Path}", plistPath);
                return null;
            }
        }

        internal static Dictionary<string, object>? ParseXmlPlistString(string xmlContent)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xmlContent);

            var rootDict = doc.SelectSingleNode("/plist/dict");
            if (rootDict == null)
                return null;

            return XCFrameworkResolver.ParsePlistDict(rootDict);
        }
    }
}
