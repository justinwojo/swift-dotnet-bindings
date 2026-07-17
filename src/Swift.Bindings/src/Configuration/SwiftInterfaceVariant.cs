// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Classifies the <c>.swiftinterface</c> variants swiftc emits alongside a framework's
    /// binary <c>.swiftmodule</c>. A module can ship up to three textual interfaces per
    /// target triple, distinguished by an access-level qualifier inserted before the
    /// extension:
    /// <list type="bullet">
    /// <item><c>&lt;triple&gt;.swiftinterface</c> — the public surface.</item>
    /// <item><c>&lt;triple&gt;.package.swiftinterface</c> — public + <c>package</c> declarations.</item>
    /// <item><c>&lt;triple&gt;.private.swiftinterface</c> — public + <c>@_spi</c> declarations.</item>
    /// </list>
    /// Bindings project the public surface, so the public variant is the only correct input
    /// for parsing and for the shadow-framework precompile. The qualified variants are also
    /// booby-trapped for path arithmetic: the file name carries an extra dot segment, so
    /// deriving a sibling artifact's name from it (e.g. swapping <c>.swiftinterface</c> for
    /// <c>.swiftmodule</c>) silently produces a name nothing looks for.
    /// </summary>
    internal static class SwiftInterfaceVariant
    {
        private const string InterfaceSuffix = ".swiftinterface";

        /// <summary>Access-level-qualified variants, i.e. everything that is not the public surface.</summary>
        private static readonly string[] NonPublicSuffixes =
        {
            ".private" + InterfaceSuffix,
            ".package" + InterfaceSuffix,
        };

        /// <summary>
        /// True when <paramref name="path"/> names the public textual interface — a
        /// <c>.swiftinterface</c> carrying no access-level qualifier segment.
        /// </summary>
        internal static bool IsPublic(string path)
        {
            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(InterfaceSuffix, StringComparison.OrdinalIgnoreCase))
                return false;

            foreach (var suffix in NonPublicSuffixes)
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }
}
