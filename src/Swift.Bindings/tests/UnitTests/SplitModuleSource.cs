// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Reads a module's full generated C# source across the file-per-top-level-type split.
    /// The emitter writes the module prelude to <c>{module}.cs</c> and every top-level type to
    /// its own <c>{module}.Types.{Leaf}.cs</c>; a consumer that needs the whole module (e.g.
    /// scanning for an emitted P/Invoke or a projected member) must read the prelude plus every
    /// type file. Mirrors the build's <c>ReadModuleCsSource</c> so tests and the parity gate see
    /// identical text. Files are concatenated in Ordinal order for determinism.
    /// </summary>
    internal static class SplitModuleSource
    {
        /// <summary>The combined text, or the empty string when the prelude is absent.</summary>
        public static string ReadAll(string outputDir, string moduleName)
        {
            var parts = new List<string>();
            var preludePath = Path.Combine(outputDir, $"{moduleName}.cs");
            if (File.Exists(preludePath))
                parts.Add(File.ReadAllText(preludePath));
            foreach (var typeFile in Directory
                         .EnumerateFiles(outputDir, $"{moduleName}.Types.*.cs")
                         .OrderBy(p => p, System.StringComparer.Ordinal))
            {
                parts.Add(File.ReadAllText(typeFile));
            }
            return string.Join("\n", parts);
        }

        /// <summary>True when the module's prelude file exists (i.e. bindings were generated).</summary>
        public static bool Exists(string outputDir, string moduleName)
            => File.Exists(Path.Combine(outputDir, $"{moduleName}.cs"));
    }
}
