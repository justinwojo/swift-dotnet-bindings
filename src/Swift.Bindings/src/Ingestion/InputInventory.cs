// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// Where a module's artifacts came from, as far as the generator's inputs can prove.
/// </summary>
public enum InputSource
{
    /// <summary>The module being bound (the primary xcframework's own module).</summary>
    Primary,

    /// <summary>A sibling module supplied through a converter/graph alongside the primary.</summary>
    Sibling,

    /// <summary>A module supplied explicitly via <c>--framework-dependency</c>.</summary>
    ExplicitDependency,

    /// <summary>A module resolved from the platform SDK / a built-in dependency database.</summary>
    AppleSdk,

    /// <summary>A Swift runtime builtin (<c>Swift</c>, <c>_Concurrency</c>, <c>simd</c>, …).</summary>
    RuntimeBuiltin,
}

/// <summary>
/// The artifacts the generator has on hand for one Swift module, receipt-neutral: it records only
/// what is actually present as input, never an inference about why something is absent.
/// </summary>
public sealed record InputModuleArtifacts
{
    /// <summary>The Swift module name.</summary>
    public required string ModuleName { get; init; }

    /// <summary>Where these artifacts came from.</summary>
    public required InputSource Source { get; init; }

    /// <summary>Path to the module's <c>.swiftinterface</c>, when one is available.</summary>
    public string? SwiftInterfacePath { get; init; }

    /// <summary>Path to the module's ABI JSON, when available.</summary>
    public string? AbiJsonPath { get; init; }

    /// <summary>Path to the module's TBD, when available.</summary>
    public string? TbdPath { get; init; }

    /// <summary>Path to the module's dylib / binary, when available.</summary>
    public string? BinaryPath { get; init; }

    /// <summary>Path to the module's xcframework, when the input was an xcframework.</summary>
    public string? XCFrameworkPath { get; init; }

    /// <summary>The <c>-F</c> framework search path swiftc will use to find this module, when known.</summary>
    public string? FrameworkSearchPath { get; init; }

    /// <summary>The managed binding-package identity this module maps to (e.g. a NuGet package id), when known.</summary>
    public string? ManagedPackageId { get; init; }

    /// <summary>
    /// Advisory provenance identity for this module (e.g. a converter's record of how it produced the
    /// artifact). Advisory ONLY — its presence never proves an artifact exists and its absence never
    /// proves a conversion failed. Populated by the converter-manifest / receipt adapters; null otherwise.
    /// </summary>
    public string? ProvenanceIdentity { get; init; }
}

/// <summary>
/// A receipt-neutral description of everything supplied to one binding-generation run: the primary
/// module plus every dependency module the generator was handed. It is the single input the
/// <see cref="BindingInputGraph"/> and the closure preflight are built from, so those consumers never
/// touch CLI/xcframework specifics directly.
/// </summary>
/// <remarks>
/// <para><b>Adapters, not constructors.</b> An inventory is produced by an adapter that translates a
/// concrete input shape into this neutral form:</para>
/// <list type="bullet">
/// <item><see cref="FromCliInvocation"/> — the current CLI / xcframework inputs (primary artifacts +
/// resolved <see cref="FrameworkDependencyInfo"/> dependencies).</item>
/// <item><see cref="WithConverterProvenance"/> — overlays advisory provenance from a converter
/// manifest (v1). A manifest can never prove absence and survives a failed run, so it contributes
/// provenance identity ONLY, never presence.</item>
/// </list>
/// <para>A session-02 conversion-<i>receipt</i> adapter attaches here later the same way; because the
/// inventory is receipt-neutral, none of the graph/preflight code changes when it lands. Diagnostics
/// must phrase absence accordingly: with no receipt, say "required module not supplied; conversion
/// provenance unavailable" — never "conversion failed to produce it".</para>
/// </remarks>
public sealed record InputInventory
{
    /// <summary>The module being bound.</summary>
    public required InputModuleArtifacts Primary { get; init; }

    /// <summary>Every dependency module supplied alongside the primary.</summary>
    public required IReadOnlyList<InputModuleArtifacts> Dependencies { get; init; }

    /// <summary>Primary + dependencies, in that order.</summary>
    public IEnumerable<InputModuleArtifacts> AllModules() => new[] { Primary }.Concat(Dependencies);

    /// <summary>
    /// Locates a supplied module's artifacts by name (ordinal), or null if not part of the inventory.
    /// </summary>
    public InputModuleArtifacts? FindModule(string moduleName) =>
        AllModules().FirstOrDefault(m => string.Equals(m.ModuleName, moduleName, System.StringComparison.Ordinal));

    /// <summary>
    /// Builds an inventory from the current CLI / xcframework inputs to <c>GenerateBindings</c>.
    /// The primary's <see cref="InputModuleArtifacts.FrameworkSearchPath"/> is derived from the primary
    /// dylib's framework directory; each dependency's is taken from the slice-selected search path on its
    /// <see cref="FrameworkDependencyInfo"/> (<paramref name="preferDeviceSlice"/> chooses which).
    /// </summary>
    public static InputInventory FromCliInvocation(
        string primaryModuleName,
        string? primarySwiftInterfacePath,
        string? primaryDylibPath,
        string? primaryAbiJsonPath,
        string? primaryTbdPath,
        string? primaryXcframeworkPath,
        IReadOnlyList<FrameworkDependencyInfo>? resolvedDependencies,
        bool preferDeviceSlice = false)
    {
        var primary = new InputModuleArtifacts
        {
            ModuleName = primaryModuleName,
            Source = InputSource.Primary,
            SwiftInterfacePath = NullIfMissing(primarySwiftInterfacePath),
            AbiJsonPath = NullIfMissing(primaryAbiJsonPath),
            TbdPath = NullIfMissing(primaryTbdPath),
            BinaryPath = NullIfMissing(primaryDylibPath),
            XCFrameworkPath = primaryXcframeworkPath,
            FrameworkSearchPath = DeriveFrameworkSearchPath(primaryDylibPath),
        };

        var deps = new List<InputModuleArtifacts>();
        if (resolvedDependencies != null)
        {
            foreach (var dep in resolvedDependencies)
            {
                var searchPath = preferDeviceSlice
                    ? dep.DeviceFrameworkSearchPath ?? dep.SimulatorFrameworkSearchPath
                    : dep.SimulatorFrameworkSearchPath ?? dep.DeviceFrameworkSearchPath;

                deps.Add(new InputModuleArtifacts
                {
                    ModuleName = dep.ModuleName,
                    Source = InputSource.ExplicitDependency,
                    SwiftInterfacePath = LocateModuleSwiftInterface(searchPath, dep.ModuleName),
                    AbiJsonPath = NullIfMissing(dep.AbiJsonPath),
                    TbdPath = NullIfMissing(dep.TbdPath),
                    BinaryPath = NullIfMissing(dep.DylibPath),
                    XCFrameworkPath = dep.XCFrameworkPath,
                    FrameworkSearchPath = searchPath,
                    ManagedPackageId = dep.IsObjCOnly ? null : dep.EffectivePackageId,
                });
            }
        }

        return new InputInventory { Primary = primary, Dependencies = deps };
    }

    /// <summary>
    /// Returns a copy of this inventory with advisory converter provenance overlaid onto any module
    /// whose name appears in <paramref name="provenanceByModule"/>. Provenance is advisory-only: it
    /// attaches an identity string and never adds, removes, or vouches for the presence of any artifact.
    /// </summary>
    public InputInventory WithConverterProvenance(IReadOnlyDictionary<string, string> provenanceByModule)
    {
        System.ArgumentNullException.ThrowIfNull(provenanceByModule);

        InputModuleArtifacts Overlay(InputModuleArtifacts m) =>
            provenanceByModule.TryGetValue(m.ModuleName, out var identity) && !string.IsNullOrEmpty(identity)
                ? m with { ProvenanceIdentity = identity }
                : m;

        return this with
        {
            Primary = Overlay(Primary),
            Dependencies = Dependencies.Select(Overlay).ToList(),
        };
    }

    private static string? NullIfMissing(string? path) =>
        !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;

    // The primary module's -F search path is its slice directory. The dylib layout is dual-shaped:
    //   * framework-wrapped: <slice>/<Module>.framework/<Module>  -> the slice dir is the .framework's parent
    //   * bare binary:       <slice>/lib<Module>.dylib            -> the slice dir is the dylib's own directory
    // Walk up to a containing *.framework segment and take its parent when present; otherwise fall back
    // to the dylib's directory. (Naive dirname(dirname(dylib)) is wrong for the bare-binary shape — it
    // lands on the xcframework root, not the slice.) Returns null when there is no dylib to derive from.
    internal static string? DeriveFrameworkSearchPath(string? primaryDylibPath)
    {
        if (string.IsNullOrEmpty(primaryDylibPath))
            return null;

        var dir = Path.GetDirectoryName(primaryDylibPath);
        while (dir != null)
        {
            if (dir.EndsWith(".framework", System.StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(dir);                 // parent of the .framework segment = slice
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            // Only walk up through .framework wrappers (Foo.framework/Versions/A/Foo). A non-framework
            // parent means the binary is a bare dylib and its own directory is the slice root.
            if (!dir.EndsWith(".framework", System.StringComparison.OrdinalIgnoreCase) &&
                (parent == null || !parent.EndsWith(".framework", System.StringComparison.OrdinalIgnoreCase)))
                break;
            dir = parent;
        }
        return Path.GetDirectoryName(primaryDylibPath);            // bare-binary slice root
    }

    // Locates a supplied module's PUBLIC swiftinterface. A framework-wrapped module keeps it at
    // <searchPath>/<Module>.framework/Modules/<Module>.swiftmodule/*.swiftinterface; a bare-binary slice
    // keeps a Modules/ dir at the slice root. The .swiftmodule directory name is not assumed to equal the
    // module name (it can differ). Prefers the public interface over the .private/.package variants so the
    // extracted edges match the public surface the wrapper re-emits. Returns null when none is found.
    internal static string? LocateModuleSwiftInterface(string? frameworkSearchPath, string moduleName)
    {
        if (string.IsNullOrEmpty(frameworkSearchPath))
            return null;

        var candidateModulesDirs = new[]
        {
            Path.Combine(frameworkSearchPath, $"{moduleName}.framework", "Modules"),
            Path.Combine(frameworkSearchPath, "Modules"),
        };

        foreach (var modulesDir in candidateModulesDirs)
        {
            if (!Directory.Exists(modulesDir))
                continue;
            foreach (var swiftModuleDir in Directory.GetDirectories(modulesDir, "*.swiftmodule"))
            {
                var interfaces = Directory.GetFiles(swiftModuleDir, "*.swiftinterface");
                var picked = interfaces.FirstOrDefault(IsPublicSwiftInterface) ?? interfaces.FirstOrDefault();
                if (picked != null)
                    return picked;
            }
        }
        return null;
    }

    private static bool IsPublicSwiftInterface(string path) =>
        !path.EndsWith(".private.swiftinterface", System.StringComparison.Ordinal) &&
        !path.EndsWith(".package.swiftinterface", System.StringComparison.Ordinal);
}
