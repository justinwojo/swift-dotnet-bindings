// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Versioned ABI handshake between a generated binding assembly and this runtime.
/// </summary>
/// <remarks>
/// <para>
/// The generated <c>[ModuleInitializer]</c> calls <see cref="AssertCompatible"/> with the
/// contract <em>epoch</em> the generator emitted against, as the single unconditional check before
/// its best-effort (try/catch) factory registrations. If the loaded runtime cannot speak that
/// epoch the call throws loudly at module load, instead of letting an incompatible binding silently
/// fall through to a later <c>MissingMethodException</c> or wrong-behavior bug deep in the dispatch
/// path. Because a module-initializer throw is uncatchable, this gate must fire <em>only</em> on a
/// genuinely-incompatible pairing — an over-strict gate is an app-wide hard abort at load.
/// </para>
/// <para>
/// <b>Epoch = package minor (derived, not hand-maintained).</b> <see cref="Version"/> and the
/// generator's <c>EmittedRuntimeContractVersion</c> are both derived from the same single-sourced
/// package version as <c>major*1000 + minor</c> (see <c>RuntimeVersionRange.Epoch</c>), so a patch
/// release never changes the epoch and the contract integer can no longer silently drift from the
/// package minor. This is the same <c>major.minor</c> boundary the bounded <c>SwiftBindings.Runtime</c>
/// NuGet range fractures on: a normal <c>PackageReference</c> consumer can never restore a
/// cross-minor binding+runtime pair (NuGet <c>NU1107</c>) in the first place, so this load gate is
/// the backstop for the paths that bypass NuGet restore — NativeAOT slice selection, direct
/// <c>ProjectReference</c> diamonds, single-file/static bundles, mixed-pack harnesses. A
/// dev/in-tree build (<c>0.0.0-dev</c>) has epoch 0; the gate treats epoch 0 on either side as
/// always-compatible, because such builds are consumed by <c>ProjectReference</c> and are
/// self-consistent by construction.
/// </para>
/// <para>
/// <b>Supported window.</b> The gate fails in two directions, for two different reasons:
/// <list type="bullet">
/// <item><description><b>Forward (<c>generatedAgainstVersion &gt; Version</c>)</b> — a binding built
/// against a newer epoch than this runtime. Its initializer calls runtime registration entrypoints
/// (e.g. <c>RegisterPayloadSemantics</c>) this older runtime does not define, and its types were
/// generated for a dispatch shape this runtime predates. Hard missing-symbol / mis-dispatch; fatal.</description></item>
/// <item><description><b>Too old (<c>generatedAgainstVersion &lt; MinimumSupportedGeneratedVersion</c>)</b>
/// — a binding built before the current dispatch contract was established. The payload-construction
/// semantics seam reads each <c>ISwiftObject</c> type's declared <c>PayloadConstructionSemantics</c>
/// (via the dispatcher cache, falling back to a reflection backstop); a binding generated before
/// that member existed cannot supply it, so the backstop would throw at first use rather than
/// declare a semantics. Reject at load instead of limping to a use-time crash.</description></item>
/// </list>
/// A binding whose epoch falls inside <c>[MinimumSupportedGeneratedVersion, Version]</c> is
/// accepted: an older-but-still-supported binding on a newer runtime resolves its declared
/// semantics through the reflection backstop and dispatches correctly. The floor moves forward only
/// when a future minor introduces a real dispatch-contract break — it is the one value here that is
/// a deliberate semantic judgment rather than a derivation.
/// </para>
/// <para>
/// <b>Bump discipline.</b> <see cref="Version"/> and <c>EmittedRuntimeContractVersion</c> track the
/// package minor automatically. Raise <see cref="MinimumSupportedGeneratedVersion"/> to the current
/// minor whenever you make a breaking change to the module-init ↔ runtime dispatch contract — the
/// signature/semantics of the dispatcher registration APIs (<c>RegisterSwiftObjectFactory</c>,
/// <c>RegisterConformanceFactory</c>, <c>RegisterWitnessTable</c>, <c>RegisterPayloadSemantics</c>),
/// the <c>ISwiftObject</c> surface generated bindings implement, or the cache-lookup expectations
/// callers rely on. Such breaks are only ever introduced at a minor boundary (a patch is
/// ABI-additive only). Pure additive changes leave the floor where it is, so older bindings keep
/// loading. The floor↔minor and lockstep relationships are enforced by a unit guard, not convention.
/// </para>
/// </remarks>
public static partial class RuntimeContract
{
    /// <summary>
    /// The dispatch/module-init contract epoch implemented by this runtime assembly, derived from
    /// this package's minor (<c>major*1000 + minor</c>). A dev/in-tree build is epoch 0.
    /// </summary>
    public static readonly int Version = ParseEpoch(BuildVersion);

    /// <summary>
    /// The oldest generated-binding epoch this runtime still supports. Bindings built against an
    /// epoch below this floor were generated before the current dispatch contract was established
    /// and cannot be dispatched correctly, so they are rejected at module load. This is a
    /// deliberate semantic value: raise it to the current minor only when a minor introduces a real
    /// dispatch-contract break (see the bump discipline above). Epoch 16 = <c>0.16</c>, the minor
    /// the payload-construction-semantics dispatch contract was established in; every binding
    /// shipped before it (<c>0.15</c> and earlier) predates the contract and is correctly rejected.
    /// </summary>
    public const int MinimumSupportedGeneratedVersion = 16;

    /// <summary>
    /// Asserts that a generated binding built against <paramref name="generatedAgainstVersion"/>
    /// is compatible with this runtime. Throws <see cref="SwiftRuntimeContractMismatchException"/>
    /// when the binding's epoch falls outside this runtime's supported window.
    /// </summary>
    /// <param name="generatedAgainstVersion">
    /// The <see cref="Version"/> epoch the generator emitted into the binding's module initializer.
    /// </param>
    public static void AssertCompatible(int generatedAgainstVersion)
    {
        if (!IsGeneratedVersionSupported(generatedAgainstVersion, Version, MinimumSupportedGeneratedVersion))
            throw new SwiftRuntimeContractMismatchException(
                generatedAgainstVersion, Version, MinimumSupportedGeneratedVersion);
    }

    /// <summary>
    /// Pure supported-window test, factored out so the gate's logic is unit-testable independently
    /// of this build's derived <see cref="Version"/> (which is the always-compatible epoch 0 in a
    /// dev build, where the real <see cref="AssertCompatible"/> can never exercise the comparisons).
    /// </summary>
    /// <remarks>
    /// Epoch 0 on either side is the dev/in-tree sentinel: an unversioned build is consumed by
    /// <c>ProjectReference</c> and is self-consistent, so the handshake is vacuously satisfied.
    /// Otherwise the binding must fall within <c>[minimumSupported, runtimeVersion]</c>.
    /// </remarks>
    internal static bool IsGeneratedVersionSupported(
        int generatedAgainstVersion, int runtimeVersion, int minimumSupported)
    {
        if (runtimeVersion == 0 || generatedAgainstVersion == 0)
            return true;
        if (generatedAgainstVersion > runtimeVersion)
            return false;
        if (generatedAgainstVersion < minimumSupported)
            return false;
        return true;
    }

    /// <summary>
    /// Maps a package version to its contract epoch (<c>major*1000 + minor</c>). Mirrors
    /// <c>BindingsGeneration.RuntimeVersionRange.Epoch</c> on the generator side — the runtime
    /// cannot reference generator code, so the two parsers are kept in lockstep by a unit guard.
    /// Returns 0 (the dev sentinel / always-compatible) on any version that is not <c>major.minor.*</c>.
    /// </summary>
    internal static int ParseEpoch(string version)
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
}

/// <summary>
/// Thrown when a generated binding's runtime-contract epoch falls outside the supported window of
/// the loaded <c>SwiftBindings.Runtime</c> assembly (<see cref="RuntimeContract.Version"/> ceiling,
/// <see cref="RuntimeContract.MinimumSupportedGeneratedVersion"/> floor).
/// </summary>
public sealed class SwiftRuntimeContractMismatchException : SwiftRuntimeException
{
    /// <summary>The contract epoch the generated binding was built against.</summary>
    public int GeneratedAgainstVersion { get; }

    /// <summary>The contract epoch implemented by the loaded runtime.</summary>
    public int RuntimeVersion { get; }

    /// <summary>The oldest generated-binding epoch the loaded runtime still supports.</summary>
    public int MinimumSupportedGeneratedVersion { get; }

    public SwiftRuntimeContractMismatchException(
        int generatedAgainstVersion, int runtimeVersion, int minimumSupportedGeneratedVersion)
        : base($"Swift binding runtime-contract mismatch: the binding was generated against runtime " +
               $"contract epoch {generatedAgainstVersion}, but the loaded SwiftBindings.Runtime implements " +
               $"epoch {runtimeVersion} and supports bindings down to epoch {minimumSupportedGeneratedVersion}. " +
               $"Regenerate the binding against the matching runtime, or align the SwiftBindings.Sdk and " +
               $"SwiftBindings.Runtime package versions.")
    {
        GeneratedAgainstVersion = generatedAgainstVersion;
        RuntimeVersion = runtimeVersion;
        MinimumSupportedGeneratedVersion = minimumSupportedGeneratedVersion;
    }
}
