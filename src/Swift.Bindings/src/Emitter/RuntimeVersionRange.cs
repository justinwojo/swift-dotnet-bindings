// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Single source of truth for the bounded NuGet version range used in emitted
    /// <c>PackageReference Include="SwiftBindings.Runtime"</c> entries and stamped
    /// into <c>Sdk.props</c>'s <c>SwiftRuntimePackageVersionRange</c>. Shared (via
    /// link-compile) with the Nuke build project so the generator-emitted range
    /// and the SDK-stamped range cannot drift.
    /// </summary>
    /// <remarks>
    /// The range floats forward across SwiftBindings.Runtime patch releases
    /// (so a 0.8.1 ABI-compatible bug fix reaches every consumer without a
    /// matrix republish) but slams shut at the next minor so a future 0.9.0
    /// with any ABI/struct-layout/P/Invoke break cannot silently hose older
    /// bindings' consumers. Plain <c>Version="0.8.0"</c> is NuGet-interpreted
    /// as a minimum-only float, which would happily resolve a future-
    /// incompatible 0.9.0 cached locally.
    /// </remarks>
    internal static class RuntimeVersionRange
    {
        /// <summary>
        /// Builds the bounded version range string for a concrete runtime version.
        /// Falls back to the raw input when the version cannot be parsed as
        /// <c>major.minor.*</c> so unit tests and pre-release strings degrade
        /// gracefully rather than producing a malformed range NuGet would reject.
        /// </summary>
        public static string Build(string version)
        {
            var firstDot = version.IndexOf('.');
            if (firstDot <= 0) return version;
            var majorStr = version.Substring(0, firstDot);
            // Validate the major component is a plain integer too — `"x.8.0"` would
            // otherwise produce `[x.8.0,x.9.0)`, which NuGet rejects at restore time.
            // Both halves must parse cleanly or the range is meaningless.
            if (!int.TryParse(majorStr, out _)) return version;
            var rest = version.Substring(firstDot + 1);
            var secondDot = rest.IndexOf('.');
            // The substring before the second dot may carry a pre-release suffix (e.g. "8-preview"),
            // but minor must be a plain integer for the +1 to make sense — so strip nothing,
            // and reject the input via TryParse below if it isn't a clean integer.
            var minorStr = secondDot < 0 ? rest : rest.Substring(0, secondDot);
            if (!int.TryParse(minorStr, out var minor)) return version;
            return $"[{version},{majorStr}.{minor + 1}.0)";
        }

        /// <summary>
        /// Builds a minimum-only NuGet range <c>[X.Y.Z,)</c> — a floor with no ceiling.
        /// Used only for the <c>SwiftBindings.Apple</c> supplement's outbound
        /// <c>SwiftBindings.Runtime</c> dependency: the supplement is always brokered
        /// by <c>SwiftBindings.Sdk</c>, whose own bounded Runtime <c>PackageReference</c>
        /// is the actual compatibility contract. The supplement therefore only needs to
        /// declare a floor, which lets a single shipped supplement nupkg ride forward
        /// across Runtime/SDK minor bumps without a no-op repack.
        /// </summary>
        /// <remarks>
        /// Do NOT use this for SDK-stamped or generator-emitted Runtime references —
        /// those are consumed directly and need the bounded form's "minor may break ABI"
        /// guarantee. Falls back to the raw input on unparseable major/minor for the
        /// same reason <see cref="Build"/> does.
        /// </remarks>
        public static string BuildMinimumOnly(string version)
        {
            var firstDot = version.IndexOf('.');
            if (firstDot <= 0) return version;
            var majorStr = version.Substring(0, firstDot);
            if (!int.TryParse(majorStr, out _)) return version;
            var rest = version.Substring(firstDot + 1);
            var secondDot = rest.IndexOf('.');
            var minorStr = secondDot < 0 ? rest : rest.Substring(0, secondDot);
            if (!int.TryParse(minorStr, out _)) return version;
            return $"[{version},)";
        }
    }
}
