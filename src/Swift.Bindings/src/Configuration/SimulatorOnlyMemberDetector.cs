// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Detects Swift members that exist only in the simulator slice of an xcframework.
    /// These members are behind #if targetEnvironment(simulator) in the Swift source.
    /// The wrapper Swift file must guard @_cdecl functions for these members so the
    /// device slice compiles successfully.
    /// </summary>
    public static class SimulatorOnlyMemberDetector
    {
        /// <summary>
        /// Compares simulator and device ABI JSON files to find members that exist only
        /// in the simulator slice. Returns qualified member names (e.g., "TypeName.propertyName").
        /// </summary>
        public static HashSet<string> Detect(
            string simulatorAbiJsonPath,
            string? deviceAbiJsonPath,
            ILogger logger)
        {
            if (string.IsNullOrEmpty(deviceAbiJsonPath) || !File.Exists(deviceAbiJsonPath))
                return new HashSet<string>();

            if (!File.Exists(simulatorAbiJsonPath))
                return new HashSet<string>();

            try
            {
                var simMembers = ExtractMembers(simulatorAbiJsonPath);
                var deviceMembers = ExtractMembers(deviceAbiJsonPath);

                var simulatorOnly = new HashSet<string>(simMembers, StringComparer.Ordinal);
                simulatorOnly.ExceptWith(deviceMembers);

                if (simulatorOnly.Count > 0)
                {
                    logger.LogInformation("Detected {Count} simulator-only member(s): {Members}",
                        simulatorOnly.Count, string.Join(", ", simulatorOnly));
                }

                return simulatorOnly;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to detect simulator-only members: {Message}", ex.Message);
                return new HashSet<string>();
            }
        }

        /// <summary>
        /// Extracts qualified member names (TypeName.memberName) from an ABI JSON file.
        /// Only extracts Var and Function declarations that are direct children of type declarations.
        /// </summary>
        private static HashSet<string> ExtractMembers(string abiJsonPath)
        {
            var members = new HashSet<string>(StringComparer.Ordinal);
            using var stream = File.OpenRead(abiJsonPath);
            using var doc = JsonDocument.Parse(stream);

            var root = doc.RootElement;
            if (root.TryGetProperty("ABIRoot", out var abiRoot))
                root = abiRoot;

            if (root.TryGetProperty("children", out var topChildren))
            {
                foreach (var child in topChildren.EnumerateArray())
                    WalkNode(child, "", members);
            }

            return members;
        }

        /// <summary>
        /// Recursively walks ABI JSON nodes, collecting qualified Var/Function names.
        /// </summary>
        private static void WalkNode(JsonElement node, string parentType, HashSet<string> members)
        {
            var kind = node.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

            var currentParent = parentType;
            if (kind == "TypeDecl" && !string.IsNullOrEmpty(name))
            {
                currentParent = string.IsNullOrEmpty(parentType) ? name : $"{parentType}.{name}";
            }

            if ((kind == "Var" || kind == "Function") && !string.IsNullOrEmpty(currentParent) && !string.IsNullOrEmpty(name))
            {
                members.Add($"{currentParent}.{name}");
            }

            if (node.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                    WalkNode(child, currentParent, members);
            }
        }

        /// <summary>
        /// Regex matching the comment lines that precede @_cdecl wrapper blocks.
        /// Captures the fully-qualified member path (e.g., "StripeIdentity.IdentityVerificationSheet.simulatorDocumentCameraImages").
        /// </summary>
        private static readonly Regex WrapperCommentRegex = new(
            @"// (?:Property [gs]etter|Method|Constructor|Enum case factory) @_cdecl wrapper for (.+)\.",
            RegexOptions.Compiled);

        /// <summary>
        /// Applies #if targetEnvironment(simulator) / #endif guards around @_cdecl wrapper
        /// blocks for simulator-only members in the Swift wrapper source.
        /// </summary>
        /// <param name="content">Swift wrapper file content.</param>
        /// <param name="moduleName">The module name (e.g., "StripeIdentity").</param>
        /// <param name="simulatorOnlyMembers">
        /// Set of qualified member names (e.g., "IdentityVerificationSheet.simulatorDocumentCameraImages").
        /// </param>
        /// <returns>The content with #if guards applied, and count of guarded blocks.</returns>
        public static (string Content, int GuardedCount) ApplySimulatorGuards(
            string content,
            string moduleName,
            HashSet<string> simulatorOnlyMembers)
        {
            if (simulatorOnlyMembers.Count == 0 || string.IsNullOrEmpty(content))
                return (content, 0);

            var lines = content.Split('\n');
            var output = new List<string>(lines.Length + simulatorOnlyMembers.Count * 2);
            int guardedCount = 0;
            int i = 0;

            while (i < lines.Length)
            {
                var stripped = lines[i].TrimStart();

                // Check if this line is a wrapper comment for a simulator-only member
                var match = WrapperCommentRegex.Match(stripped);
                if (match.Success)
                {
                    var qualifiedPath = match.Groups[1].Value;
                    if (IsSimulatorOnlyMember(qualifiedPath, moduleName, simulatorOnlyMembers))
                    {
                        // Find the full block: comment line(s) + optional @available + @_cdecl + func body
                        int blockStart = i;

                        // Include the comment line in the guarded block
                        // Scan forward to find @_cdecl or @_silgen_name, then find block end
                        int funcStart = i + 1;
                        while (funcStart < lines.Length)
                        {
                            var s = lines[funcStart].TrimStart();
                            if (s.StartsWith("@_cdecl(", StringComparison.Ordinal) ||
                                s.StartsWith("@_silgen_name(", StringComparison.Ordinal) ||
                                s.StartsWith("@available(", StringComparison.Ordinal) ||
                                s.StartsWith("@MainActor", StringComparison.Ordinal))
                            {
                                break;
                            }
                            if (!s.StartsWith("//", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(s))
                                break; // Not part of the block header
                            funcStart++;
                        }

                        // Find end of function body (matching braces)
                        int blockEnd = FindBlockEnd(lines, funcStart);

                        // Emit with #if guard
                        output.Add("#if targetEnvironment(simulator)");
                        for (int j = blockStart; j <= blockEnd && j < lines.Length; j++)
                            output.Add(lines[j]);
                        output.Add("#endif");

                        guardedCount++;
                        i = blockEnd + 1;
                        continue;
                    }
                }

                output.Add(lines[i]);
                i++;
            }

            return (string.Join('\n', output), guardedCount);
        }

        /// <summary>
        /// Checks if a qualified path from a wrapper comment matches a simulator-only member.
        /// The comment path may be module-qualified (e.g., "StripeIdentity.IdentityVerificationSheet.simulatorDocumentCameraImages")
        /// while the member set uses type-qualified names (e.g., "IdentityVerificationSheet.simulatorDocumentCameraImages").
        /// </summary>
        private static bool IsSimulatorOnlyMember(string qualifiedPath, string moduleName, HashSet<string> simulatorOnlyMembers)
        {
            // Try exact match first
            if (simulatorOnlyMembers.Contains(qualifiedPath))
                return true;

            // Strip module prefix and try again
            var prefix = moduleName + ".";
            if (qualifiedPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                var stripped = qualifiedPath.Substring(prefix.Length);
                if (simulatorOnlyMembers.Contains(stripped))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Creates a filtered copy of a native thunk assembly file that excludes thunks
        /// referencing simulator-only members. Used for device slice compilation.
        /// </summary>
        /// <param name="assemblyFilePath">Path to the original .arm64.s file.</param>
        /// <param name="simulatorOnlyMembers">Simulator-only member names (e.g., "IdentityVerificationSheet.simulatorDocumentCameraImages").</param>
        /// <param name="deviceOutputDirectory">Directory to write the filtered file.</param>
        /// <returns>Path to filtered file and count of removed thunks, or null if no filtering needed.</returns>
        public static (string FilteredPath, int RemovedCount)? FilterThunkAssembly(
            string assemblyFilePath,
            HashSet<string> simulatorOnlyMembers,
            string deviceOutputDirectory)
        {
            if (simulatorOnlyMembers.Count == 0)
                return null;

            // Extract just the member names (last component) for matching against mangled symbols.
            // Mangled symbols contain the member name literally (e.g., "simulatorDocumentCameraImages").
            var memberNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fqn in simulatorOnlyMembers)
            {
                var lastDot = fqn.LastIndexOf('.');
                if (lastDot >= 0)
                    memberNames.Add(fqn.Substring(lastDot + 1));
                else
                    memberNames.Add(fqn);
            }

            var lines = File.ReadAllLines(assemblyFilePath);
            var output = new List<string>(lines.Length);
            int removedCount = 0;
            int i = 0;

            while (i < lines.Length)
            {
                // Thunk blocks start with ".globl _thunk_..."
                if (lines[i].TrimStart().StartsWith(".globl _thunk_", StringComparison.Ordinal))
                {
                    // Collect the full thunk block (up to and including "ret")
                    int blockStart = i;
                    int blockEnd = i;
                    for (int j = i; j < lines.Length; j++)
                    {
                        blockEnd = j;
                        if (lines[j].TrimStart().StartsWith("ret", StringComparison.Ordinal))
                            break;
                    }

                    // Check if this thunk references a simulator-only symbol
                    bool isSimOnly = false;
                    for (int j = blockStart; j <= blockEnd; j++)
                    {
                        foreach (var memberName in memberNames)
                        {
                            if (lines[j].Contains(memberName, StringComparison.Ordinal))
                            {
                                isSimOnly = true;
                                break;
                            }
                        }
                        if (isSimOnly) break;
                    }

                    if (isSimOnly)
                    {
                        removedCount++;
                        i = blockEnd + 1;
                        continue;
                    }
                }

                output.Add(lines[i]);
                i++;
            }

            if (removedCount == 0)
                return null;

            var filteredPath = Path.Combine(deviceOutputDirectory, Path.GetFileName(assemblyFilePath));
            File.WriteAllLines(filteredPath, output);
            return (filteredPath, removedCount);
        }

        /// <summary>
        /// Finds the end of a function block by tracking brace depth.
        /// </summary>
        private static int FindBlockEnd(string[] lines, int start)
        {
            int depth = 0;
            bool sawOpenBrace = false;
            for (int j = start; j < lines.Length; j++)
            {
                foreach (char c in lines[j])
                {
                    if (c == '{') { depth++; sawOpenBrace = true; }
                    else if (c == '}') depth--;
                }
                if (sawOpenBrace && depth <= 0 && j > start)
                    return j;
            }
            return lines.Length - 1;
        }
    }
}
