// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime;

/// <summary>
/// Versioned ABI handshake between a generated binding assembly and this runtime.
/// </summary>
/// <remarks>
/// <para>
/// The generated <c>[ModuleInitializer]</c> calls <see cref="AssertCompatible"/> with the
/// contract version the generator emitted against, as the single unconditional check before
/// its best-effort (try/catch) factory registrations. If the loaded runtime implements a
/// different contract version the call throws loudly at module load, instead of letting an
/// incompatible binding silently fall through to a later <c>MissingMethodException</c> or
/// wrong-behavior bug deep in the dispatch path.
/// </para>
/// <para>
/// <b>Bump discipline (mirrors the SwiftInterfaceParser schema-version handshake):</b> raise
/// <see cref="Version"/> here AND the generator's <c>EmittedRuntimeContractVersion</c>
/// (ModuleHandler) in lockstep whenever the module-init ↔ runtime dispatch contract changes
/// shape — e.g. the signature/semantics of the dispatcher registration APIs
/// (<c>RegisterSwiftObjectFactory</c>, <c>RegisterConformanceFactory</c>,
/// <c>RegisterWitnessTable</c>) or the cache-lookup expectations callers rely on. Pure additive
/// changes that keep existing generated initializers valid do not require a bump.
/// </para>
/// </remarks>
public static class RuntimeContract
{
    /// <summary>
    /// The dispatch/module-init contract version implemented by this runtime assembly.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Asserts that a generated binding built against <paramref name="generatedAgainstVersion"/>
    /// is compatible with this runtime. Throws <see cref="SwiftRuntimeContractMismatchException"/>
    /// on any mismatch.
    /// </summary>
    /// <param name="generatedAgainstVersion">
    /// The <see cref="Version"/> value the generator emitted into the binding's module initializer.
    /// </param>
    public static void AssertCompatible(int generatedAgainstVersion)
    {
        if (generatedAgainstVersion != Version)
            throw new SwiftRuntimeContractMismatchException(generatedAgainstVersion, Version);
    }
}

/// <summary>
/// Thrown when a generated binding's runtime-contract version does not match the loaded
/// <c>SwiftBindings.Runtime</c> assembly's <see cref="RuntimeContract.Version"/>.
/// </summary>
public sealed class SwiftRuntimeContractMismatchException : SwiftRuntimeException
{
    /// <summary>The contract version the generated binding was built against.</summary>
    public int GeneratedAgainstVersion { get; }

    /// <summary>The contract version implemented by the loaded runtime.</summary>
    public int RuntimeVersion { get; }

    public SwiftRuntimeContractMismatchException(int generatedAgainstVersion, int runtimeVersion)
        : base($"Swift binding runtime-contract mismatch: the binding was generated against runtime " +
               $"contract version {generatedAgainstVersion}, but the loaded SwiftBindings.Runtime implements " +
               $"version {runtimeVersion}. Regenerate the binding against the matching runtime, or align the " +
               $"SwiftBindings.Sdk and SwiftBindings.Runtime package versions.")
    {
        GeneratedAgainstVersion = generatedAgainstVersion;
        RuntimeVersion = runtimeVersion;
    }
}
