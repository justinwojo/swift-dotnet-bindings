// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration
{
    /// <summary>
    /// Deterministic file naming for the file-per-top-level-type split. The module
    /// prelude keeps the historical <c>{Namespace}.cs</c> name; each top-level type
    /// gets <c>{Namespace}.Types.{Leaf}.cs</c>. The <c>.Types.</c> infix keeps the
    /// per-type files clear of every reserved/companion name (<c>ApiDefinition.cs</c>,
    /// <c>StructsAndEnums.cs</c>, <c>{Namespace}.cs</c>, <c>{Namespace}.Wrappers.cs</c>,
    /// <c>{Namespace}.SwiftUIBridge.cs</c>, <c>{Namespace}.SwiftInterop.cs</c>) and gives
    /// a single wildcard — <c>{Namespace}.Types.*.cs</c> — that cleans stale files and
    /// feeds the standalone csproj / harness Compile globs.
    /// </summary>
    internal static class SplitFileNaming
    {
        /// <summary>The infix that marks a per-type split file. See class remarks.</summary>
        public const string TypesInfix = ".Types.";

        /// <summary>
        /// The C# leaf name for a top-level type, matching the name the emitter actually
        /// declared (the TypeDatabase-recorded, collision-renamed C# name when available,
        /// else a PascalCase fallback). Not yet filesystem-sanitized or disambiguated —
        /// callers pass this through <see cref="SanitizeLeaf"/> and a case-insensitive
        /// collision counter.
        /// </summary>
        public static string LeafFor(BaseDecl decl, ITypeDatabase typeDatabase)
        {
            if (decl is TypeDecl typeDecl)
            {
                if (typeDecl.SwiftTypeName != null &&
                    typeDatabase.TryGetTypeRecord(typeDecl.SwiftTypeName, out var record))
                {
                    var csLeaf = StripToLeaf(record.CSharpTypeName.Name);
                    if (!string.IsNullOrEmpty(csLeaf))
                        return csLeaf;
                }
            }

            var name = decl.Name;
            return string.IsNullOrEmpty(name)
                ? "Type"
                : NameProvider.ToPascalCaseForTypeName(name);
        }

        /// <summary>
        /// Reduces a possibly-qualified, possibly-generic C# type name to its leaf
        /// identifier: drops any <c>&lt;...&gt;</c> generic-argument suffix and any
        /// <c>Outer.Inner</c> qualifier prefix.
        /// </summary>
        private static string StripToLeaf(string csharpTypeName)
        {
            var name = csharpTypeName;
            var angle = name.IndexOf('<');
            if (angle >= 0)
                name = name.Substring(0, angle);
            var dot = name.LastIndexOf('.');
            if (dot >= 0)
                name = name.Substring(dot + 1);
            return name.Trim();
        }

        /// <summary>
        /// Maps a C# leaf to a filesystem-safe token: keeps ASCII letters/digits/underscore,
        /// replaces everything else with <c>_</c>. Deterministic and stable across regens.
        /// </summary>
        public static string SanitizeLeaf(string leaf)
        {
            if (string.IsNullOrEmpty(leaf))
                return "Type";
            var sb = new StringBuilder(leaf.Length);
            foreach (var ch in leaf)
                sb.Append((ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || ch == '_') ? ch : '_');
            var sanitized = sb.ToString();
            return string.IsNullOrEmpty(sanitized) ? "Type" : sanitized;
        }

        /// <summary>The per-type file name: <c>{Namespace}.Types.{Leaf}.cs</c>.</summary>
        public static string TypeFileName(string @namespace, string disambiguatedLeaf)
            => $"{@namespace}{TypesInfix}{disambiguatedLeaf}.cs";

        /// <summary>The wildcard that matches every per-type file for a namespace.</summary>
        public static string TypeFileGlob(string @namespace)
            => $"{@namespace}{TypesInfix}*.cs";
    }
}
