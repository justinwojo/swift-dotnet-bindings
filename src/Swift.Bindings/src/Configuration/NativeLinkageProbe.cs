// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// How a bound native framework binary is linked: a dynamic Mach-O shared library,
    /// or a static <c>ar</c>(1) archive. This drives Gap 2's single-registration wiring —
    /// a static native is force-loaded into the Swift wrapper (the sole runtime carrier)
    /// and dropped from every consumer reference/pack site so the same ObjC classes are
    /// never embedded twice.
    /// </summary>
    public enum NativeLinkage
    {
        /// <summary>Mach-O dynamically linked shared library (one load command, one copy).</summary>
        Dynamic,

        /// <summary>Static <c>ar</c>(1) archive (thin or universal-of-archive).</summary>
        Static,
    }

    /// <summary>
    /// Classifies a native framework binary as <see cref="NativeLinkage.Static"/> or
    /// <see cref="NativeLinkage.Dynamic"/> via the <c>file</c>(1) command, with a
    /// magic-byte fallback. The single source of truth for the static-vs-dynamic decision
    /// shared by the wrapper compiler (force_load), the metadata emitter
    /// (<c>_SwiftBindingSourceNativeLinkage</c>), and the consumer-reference gating.
    ///
    /// <para>
    /// Crucially, <see cref="NativeLinkage.Static"/> is returned ONLY for a genuine
    /// <c>ar</c> archive. A Mach-O dylib is <see cref="NativeLinkage.Dynamic"/>, and so is
    /// anything that is neither (a TBD/JSON text stub, a missing file, an unreadable path).
    /// This asymmetry is deliberate: the only action keyed off <see cref="NativeLinkage.Static"/>
    /// is <c>-force_load</c>, and force-loading a non-archive (e.g. the <c>.tbd</c> the
    /// direct/Apple path passes in place of a binary) would break the link. "Don't know" must
    /// resolve to "don't force_load."
    /// </para>
    /// </summary>
    internal static class NativeLinkageProbe
    {
        /// <summary>
        /// Detects the linkage of the binary at <paramref name="binaryPath"/>. Returns
        /// <see cref="NativeLinkage.Dynamic"/> for a missing/empty path so callers never
        /// have to null-check before probing.
        /// </summary>
        public static NativeLinkage Detect(string? binaryPath, ICommandRunner commandRunner, ILogger logger)
        {
            if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
                return NativeLinkage.Dynamic;

            try
            {
                var (exitCode, stdout, _) = commandRunner.Run("file", $"\"{binaryPath}\"", timeoutMs: 10000);
                if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                    return DetectByMagic(binaryPath, logger);

                // A universal binary reports each arch; a dynamic dylib slice says
                // "dynamically linked shared library", a static slice says "current ar archive".
                if (stdout.Contains("dynamically linked shared library", StringComparison.OrdinalIgnoreCase))
                    return NativeLinkage.Dynamic;
                if (stdout.Contains("ar archive", StringComparison.OrdinalIgnoreCase))
                    return NativeLinkage.Static;

                // Anything else (JSON/TBD text stub, Mach-O bundle, unknown) — do not force_load.
                return NativeLinkage.Dynamic;
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    "NativeLinkageProbe: 'file' failed for {Path} ({Message}); falling back to magic-byte sniff.",
                    binaryPath, ex.Message);
                return DetectByMagic(binaryPath, logger);
            }
        }

        /// <summary>
        /// Fallback when <c>file</c> is unavailable: a thin <c>ar</c> archive begins with the
        /// literal <c>!&lt;arch&gt;\n</c> magic. A universal (fat) binary begins with the fat
        /// magic regardless of whether its members are dylibs or archives, so this sniff
        /// cannot classify fat-of-archive — it conservatively returns
        /// <see cref="NativeLinkage.Dynamic"/> there (no force_load) since <c>file</c> is the
        /// primary signal and only rarely unavailable.
        /// </summary>
        private static NativeLinkage DetectByMagic(string binaryPath, ILogger logger)
        {
            try
            {
                Span<byte> magic = stackalloc byte[8];
                using var fs = File.OpenRead(binaryPath);
                var read = fs.Read(magic);
                // "!<arch>\n" == 0x21 0x3C 0x61 0x72 0x63 0x68 0x3E 0x0A
                if (read >= 8 &&
                    magic[0] == 0x21 && magic[1] == 0x3C && magic[2] == 0x61 && magic[3] == 0x72 &&
                    magic[4] == 0x63 && magic[5] == 0x68 && magic[6] == 0x3E && magic[7] == 0x0A)
                {
                    return NativeLinkage.Static;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("NativeLinkageProbe: magic-byte sniff failed for {Path}: {Message}", binaryPath, ex.Message);
            }
            return NativeLinkage.Dynamic;
        }
    }
}
