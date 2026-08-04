// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Emits <c>{namespace}.api-surface.md</c> — a human-readable member table derived from the
    /// SAME emitted-surface facts as <c>{namespace}.api-manifest.json</c>, grouped by containing
    /// type. Its purpose is to give any consumer-facing README an authoritative, machine-derived
    /// member list to render from, instead of a hand-authored list transcribed from the Swift
    /// source — a hand-authored list drifts (it documents members the binding never emitted, or
    /// omits ones it did). Because this table is regenerated from what the generator actually
    /// emitted on every build, a README that derives its member list from it cannot drift.
    ///
    /// Scope note: the table lists the members the API manifest records — emitted methods, free
    /// functions, properties and subscripts. Methods and free functions are captured at the
    /// overload-disambiguation chokepoints, which give them their post-collision C# signature;
    /// properties and subscripts carry no overload disambiguation, so they are recorded at their own
    /// emission points and appear under their emitted C# name (a property with no parameter list, a
    /// subscript as <c>this[…]</c>).
    /// </summary>
    public static class ApiSurfaceDocEmitter
    {
        /// <summary>Sentinel group heading for module-scope free functions (no containing type).</summary>
        internal const string FreeFunctionsHeading = "(free functions)";

        /// <summary>
        /// Writes <c>{namespace}.api-surface.md</c> next to the generated <c>.cs</c>. No-ops
        /// (returns <c>null</c>) when there is no emission context or no recorded members —
        /// but when a prior build left a surface doc and this build records zero members, the
        /// stale file is DELETED rather than left behind: a doc that outlives the members it
        /// listed is the exact drift this artifact exists to prevent.
        /// </summary>
        public static string? Emit(string moduleName, string @namespace, ModuleEmissionContext? emissionCtx,
            string outputDirectory, ILogger logger)
        {
            if (emissionCtx is null) return null;
            var entries = emissionCtx.ApiManifestEntries;
            if (entries.Count == 0)
            {
                var stalePath = Path.Combine(outputDirectory, $"{@namespace}.api-surface.md");
                if (File.Exists(stalePath))
                {
                    File.Delete(stalePath);
                    logger.LogInformation($"Removed stale API surface doc (no members emitted) at {stalePath}");
                }
                return null;
            }

            var markdown = Render(moduleName, entries.Keys);
            var path = Path.Combine(outputDirectory, $"{@namespace}.api-surface.md");
            File.WriteAllText(path, markdown);
            logger.LogInformation($"Wrote API surface doc ({entries.Count} members) to {path}");
            return path;
        }

        /// <summary>
        /// Renders the Markdown member table from the manifest signature keys. Pure function of the
        /// key set — no I/O — so it is unit-testable directly against a manifest. Splitting a key
        /// into its containing-type path and its member is the exact inverse of how the key was
        /// built, so it is done by the key builders' own core rather than re-derived here.
        /// </summary>
        internal static string Render(string moduleName, IEnumerable<string> manifestKeys)
        {
            // group heading (containing type, or the free-functions sentinel) → member signatures.
            var groups = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var key in manifestKeys)
            {
                var (parentPath, member) = ModuleEmissionContext.SplitApiManifestKey(key);
                string heading = parentPath.Length > 0 ? parentPath : FreeFunctionsHeading;

                if (!groups.TryGetValue(heading, out var members))
                    groups[heading] = members = new SortedSet<string>(StringComparer.Ordinal);
                members.Add(member);
            }

            var sb = new StringBuilder();
            sb.Append("# ").Append(moduleName).Append(" — public API surface\n\n");
            sb.Append("<!--\n");
            sb.Append("  AUTO-GENERATED from the emitted binding surface — do not hand-edit.\n");
            sb.Append("  Regenerated on every binding build from the emitted member set (the post-collision\n");
            sb.Append("  C# signatures the generator actually emitted). Point any consumer-facing README member\n");
            sb.Append("  list at this file so documentation cannot drift from the shipped API.\n");
            sb.Append("  Scope: the emitted public members — methods, free functions, properties and subscripts.\n");
            sb.Append("  A member listed without a parameter list is a property; an indexer appears as `this[...]`.\n");
            sb.Append("-->\n");

            foreach (var (heading, members) in groups)
            {
                sb.Append("\n## ").Append(heading).Append('\n').Append('\n');
                foreach (var member in members)
                    sb.Append("- `").Append(member).Append("`\n");
            }

            return sb.ToString();
        }
    }
}
