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
    /// <para>
    /// This <c>&lt;remarks&gt;</c> is the contract of record for version coexistence (there is no
    /// separate design doc). The three packages relate as follows: <c>SwiftBindings.Runtime</c>
    /// <em>is</em> the runtime; <c>SwiftBindings.Sdk</c> and every generated binding carry a
    /// bounded <c>[X.Y.Z, X.(Y+1).0)</c> Runtime range (this method); the <c>SwiftBindings.Apple</c>
    /// supplement carries a floor-only <c>[A.B.C,)</c> range (<see cref="BuildMinimumOnly"/>),
    /// which is safe <em>only</em> because the supplement is always brokered by the SDK, whose own
    /// bounded range supplies the ceiling. Patch is ABI-additive only (no struct-layout,
    /// P/Invoke-signature, calling-convention, or public-API removal/change); a minor is allowed to
    /// break ABI, which is exactly why the window slams shut at the next minor.
    /// </para>
    /// <para>
    /// Consumer-visible consequence: two bindings built one Runtime-minor apart are mutually
    /// uninstallable in one project (NuGet <c>NU1107</c>). That is intended protection — strictly
    /// better than silently loading an ABI-incompatible runtime and crashing — but it is a real
    /// fracture boundary, so keep Runtime-minor bumps rare and batch ABI breaks into them.
    /// </para>
    /// <para>
    /// Enforcement seam: <c>EnablePackageValidation</c> on the Runtime/Apple csprojs runs NuGet's
    /// offline compatible-framework / compatible-RID validators at pack time. The cross-version
    /// ApiCompat check (<c>PackageValidationBaselineVersion</c>) and the minor-window end-state are
    /// a single coupled, deferred owner decision (a baseline would force an offline-breaking
    /// <c>PackageDownload</c>). This range is no
    /// longer the only thing standing behind the rule.
    /// </para>
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
        /// Maps a package version to its dispatch-contract <em>epoch</em> — the integer the
        /// load-time <c>RuntimeContract</c> handshake compares — as <c>major*1000 + minor</c>,
        /// single-sourced from the same <c>major.minor</c> that <see cref="Build"/> uses for the
        /// bounded NuGet range. Tying the load gate's epoch and the restore gate's range to one
        /// parse makes them fracture on the same boundary and removes the hand-maintained-literal
        /// drift the contract integer used to have.
        /// </summary>
        /// <remarks>
        /// The <c>major*1000</c> term keeps <c>0.15</c> (epoch 15) distinct from a future
        /// <c>1.15</c> (epoch 1015) and keeps a real <c>1.0.0</c> release (epoch 1000) clear of the
        /// <c>0.0.0-dev</c> sentinel (epoch 0) — the in-tree/ProjectReference build whose epoch the
        /// handshake treats as always-compatible. A pre-release suffix on the minor is ignored
        /// (<c>0.16.0-preview.1</c> → 16), matching <see cref="Build"/>'s minor-ceiling behavior.
        /// Returns 0 (the dev sentinel) when the version cannot be parsed as <c>major.minor.*</c>,
        /// so an unparseable string degrades to the always-compatible bypass rather than a
        /// malformed gate; the pack-time guard — not this load-gate epoch — is what fails closed on
        /// a malformed shipped version.
        /// </remarks>
        public static int Epoch(string version)
        {
            var firstDot = version.IndexOf('.');
            if (firstDot <= 0) return 0;
            var majorStr = version.Substring(0, firstDot);
            if (!int.TryParse(majorStr, out var major)) return 0;
            var rest = version.Substring(firstDot + 1);
            var secondDot = rest.IndexOf('.');
            var minorStr = secondDot < 0 ? rest : rest.Substring(0, secondDot);
            if (!int.TryParse(minorStr, out var minor)) return 0;
            return major * 1000 + minor;
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
