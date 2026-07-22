// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace BindingsGeneration;

/// <summary>
/// The .NET shape of an Apple SDK type as it actually appears in the Microsoft.iOS binding —
/// class (Handle-bearing NSObject), enum (integer value type), struct, static-constants class
/// (abstract sealed, e.g. <c>UIWindowLevel</c>), or protocol/interface.
/// </summary>
internal enum AppleTypeSurfaceKind
{
    Class,
    Enum,
    Struct,
    StaticConstants,
    Protocol,
}

/// <summary>
/// One resolved Microsoft.iOS type: its real .NET name and namespace, its shape, and — for
/// enums — the managed underlying integer type and whether it is a <c>[Flags]</c> bitmask.
/// </summary>
internal sealed record AppleTypeSurfaceEntry(
    string Name,
    string Namespace,
    AppleTypeSurfaceKind Kind,
    string? EnumUnderlyingType,
    bool IsFlags);

/// <summary>
/// A name→shape index of the public Apple binding surface for the target platform, built once by
/// reading the installed reference assembly (Microsoft.iOS / Microsoft.macOS / Microsoft.tvOS /
/// Microsoft.MacCatalyst, selected by <see cref="SetAmbientPlatform"/>) with a metadata-only
/// <see cref="MetadataLoadContext"/>.
/// <para>
/// The ObjC-bridging synthesis fabricates a .NET name and a Handle-bearing class shape for every
/// Apple type it can't find in the hand-maintained database. For nested enums, value types, and
/// static-constants types that synthesis is wrong — it invents names Microsoft.iOS never declares
/// (<c>UIImpactFeedbackGeneratorFeedbackStyle</c> for the real <c>UIImpactFeedbackStyle</c>) and
/// treats structs/enums as NSObjects. This index lets the synthesis check its candidate against
/// the type that actually ships: use the real name when it differs, project an integer enum as a
/// value type, and skip a member whose type genuinely isn't in the binding rather than emit a
/// dangling reference.
/// </para>
/// The reference assemblies live on disk in the installed Apple workload; the index is built at most
/// once per target platform (a per-platform cache). When the platform's workload isn't present the
/// entry is null and callers fall back to name synthesis, so generation never depends on it.
/// </summary>
internal sealed class AppleTypeSurfaceIndex
{
    // Keyed by "Namespace.Name" (exact) and by bare "Name" (first writer wins across namespaces).
    private readonly IReadOnlyDictionary<string, AppleTypeSurfaceEntry> _byFullName;
    private readonly IReadOnlyDictionary<string, AppleTypeSurfaceEntry> _byBareName;

    internal AppleTypeSurfaceIndex(
        IReadOnlyDictionary<string, AppleTypeSurfaceEntry> byFullName,
        IReadOnlyDictionary<string, AppleTypeSurfaceEntry> byBareName)
    {
        _byFullName = byFullName;
        _byBareName = byBareName;
    }

    internal int Count => _byBareName.Count;

    /// <summary>Resolves a namespace-qualified candidate exactly (e.g. <c>UIKit</c> + <c>UIImpactFeedbackStyle</c>).</summary>
    internal bool TryResolveQualified(string @namespace, string name, [NotNullWhen(true)] out AppleTypeSurfaceEntry? entry)
        => _byFullName.TryGetValue($"{@namespace}.{name}", out entry);

    /// <summary>Resolves a bare candidate name across all namespaces (first-registered wins).</summary>
    internal bool TryResolveBare(string name, [NotNullWhen(true)] out AppleTypeSurfaceEntry? entry)
        => _byBareName.TryGetValue(name, out entry);

    // The current generation's target platform, set once at generation start (BindingsGeneratorCommand)
    // so Default resolves the reference assembly that actually ships for it — a macOS binding must be
    // verified against Microsoft.macOS, not Microsoft.iOS. AsyncLocal mirrors ReportCollector's ambient
    // pattern: the index is read from static call sites with no threaded platform, and a plain static
    // field would leak one run's platform across the parallel emitter tests. Unset → iOS (the CLI's
    // own --platform default and the historical behavior).
    private static readonly AsyncLocal<ApplePlatform?> s_ambientPlatform = new();

    /// <summary>
    /// Records the target Apple platform for the current generation so <see cref="Default"/> resolves
    /// the reference assembly that ships for it. Set once at generation start; unset resolves to iOS.
    /// </summary>
    internal static void SetAmbientPlatform(ApplePlatform platform) => s_ambientPlatform.Value = platform;

    /// <summary>Clears the ambient platform so a later read falls back to iOS (test isolation).</summary>
    internal static void ResetAmbientPlatform() => s_ambientPlatform.Value = null;

    // One index per platform, each built at most once. GetOrAdd caches a null result too, so a platform
    // whose reference pack isn't installed degrades to name synthesis without re-probing every lookup.
    private static readonly ConcurrentDictionary<ApplePlatform, AppleTypeSurfaceIndex?> s_byPlatform = new();

    // A test-installed override of the resolved index for the current async flow. Production never sets
    // this — Default falls through to the per-platform reference-pack cache below. It exists so
    // synthesis/emitter tests can drive the real ingress path (ObjC-bridging synthesis →
    // TryProjectViaAppleSurface → ClassifyUnsupportedReference) against a hand-built surface — or a
    // deterministically *absent* surface (Index null, modelling the workload-not-installed fallback) —
    // without depending on whatever reference assemblies happen to be installed. A null AsyncLocal value
    // means "no override"; a present box carries the forced index, which may itself be null (the box is
    // what distinguishes "force surface-unavailable" from "no override"). AsyncLocal — like
    // s_ambientPlatform and ReportCollector's session — so one test's override never leaks into a
    // parallel test's Default read.
    private sealed class SurfaceOverride
    {
        internal AppleTypeSurfaceIndex? Index { get; }
        internal SurfaceOverride(AppleTypeSurfaceIndex? index) => Index = index;
    }

    private static readonly AsyncLocal<SurfaceOverride?> s_overrideForCurrentFlow = new();

    /// <summary>
    /// The index for the current generation's target platform (see <see cref="SetAmbientPlatform"/>), or
    /// null when that platform's reference assembly isn't installed.
    /// </summary>
    internal static AppleTypeSurfaceIndex? Default =>
        s_overrideForCurrentFlow.Value is { } o
            ? o.Index
            : GetForPlatform(s_ambientPlatform.Value ?? ApplePlatform.iOS);

    /// <summary>
    /// Test-only: installs <paramref name="index"/> as <see cref="Default"/> for the current async flow
    /// and returns a scope that restores the previous value on dispose. Passing a real index exercises
    /// the surface-verification path; passing null forces the surface-unavailable fallback
    /// deterministically (independent of which reference assemblies are installed).
    /// </summary>
    internal static IDisposable OverrideDefaultForTest(AppleTypeSurfaceIndex? index)
    {
        var previous = s_overrideForCurrentFlow.Value;
        s_overrideForCurrentFlow.Value = new SurfaceOverride(index);
        return new DefaultOverrideScope(previous);
    }

    private sealed class DefaultOverrideScope : IDisposable
    {
        private readonly SurfaceOverride? _previous;
        private bool _disposed;

        internal DefaultOverrideScope(SurfaceOverride? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            s_overrideForCurrentFlow.Value = _previous;
        }
    }

    /// <summary>The index for a specific platform, or null when its reference assembly isn't installed.</summary>
    internal static AppleTypeSurfaceIndex? GetForPlatform(ApplePlatform platform)
        => s_byPlatform.GetOrAdd(platform, BuildFromInstalledRefPack);

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MetadataLoadContext reads metadata only and never executes code; the generator never runs under NativeAOT.")]
    private static AppleTypeSurfaceIndex? BuildFromInstalledRefPack(ApplePlatform platform)
    {
        try
        {
            var appleRefAssembly = FindMicrosoftRefAssembly(platform);
            if (appleRefAssembly is null)
                return null;

            var coreRefDir = FindNetCoreAppRefDir();
            if (coreRefDir is null)
                return null;

            var refDir = Path.GetDirectoryName(appleRefAssembly)!;
            var assemblies = Directory.GetFiles(refDir, "*.dll")
                .Concat(Directory.GetFiles(coreRefDir, "*.dll"))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();

            var resolver = new PathAssemblyResolver(assemblies);
            // The Apple ref pack defines its own types but pulls core types (System.Object, …) from the
            // framework ref pack, whose runtime-facing assembly name is "System.Runtime".
            using var mlc = new MetadataLoadContext(resolver, "System.Runtime");
            var appleAsm = mlc.LoadFromAssemblyPath(appleRefAssembly);
            return BuildFromAssembly(appleAsm);
        }
        catch
        {
            // Any discovery/load failure degrades to name synthesis — never block generation.
            return null;
        }
    }

    /// <summary>
    /// Builds an index from an already-loaded assembly. Public for unit tests, which construct the
    /// index from a hand-built assembly (or exercise <see cref="AppleTypeSurfaceIndex(IReadOnlyDictionary{string, AppleTypeSurfaceEntry}, IReadOnlyDictionary{string, AppleTypeSurfaceEntry})"/>
    /// directly) rather than requiring the installed workload.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reflects over an external Microsoft.iOS reference assembly loaded from disk via MetadataLoadContext; the trimmer never sees or trims that assembly, and the generator is a build-time managed tool.")]
    internal static AppleTypeSurfaceIndex BuildFromAssembly(Assembly iosAsm)
    {
        var byFullName = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal);
        var byBareName = new Dictionary<string, AppleTypeSurfaceEntry>(StringComparer.Ordinal);

        foreach (var type in iosAsm.GetExportedTypes())
        {
            // Nested types flatten into their parent's name in ObjC bindings and aren't the
            // reference targets the synthesis produces; the top-level flattened form is.
            if (type.IsNested)
                continue;

            var ns = type.Namespace ?? string.Empty;
            var name = type.Name;
            var kind = ClassifyType(type, out var underlying, out var isFlags);
            var entry = new AppleTypeSurfaceEntry(name, ns, kind, underlying, isFlags);

            byFullName[$"{ns}.{name}"] = entry;
            if (!byBareName.ContainsKey(name))
                byBareName[name] = entry;
        }

        return new AppleTypeSurfaceIndex(byFullName, byBareName);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Reads fields/attributes of a type from an external Microsoft.iOS reference assembly loaded via MetadataLoadContext; the trimmer never trims that assembly.")]
    private static AppleTypeSurfaceKind ClassifyType(Type type, out string? underlying, out bool isFlags)
    {
        underlying = null;
        isFlags = false;

        if (type.IsEnum)
        {
            // Read the underlying integer from the enum's value field rather than
            // GetEnumUnderlyingType(), which is not supported for MetadataLoadContext types.
            var valueField = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.Name == "value__");
            var attrs = type.GetCustomAttributesData();
            // A [Native] enum is an NSInteger/NSUInteger NS_ENUM/NS_OPTIONS: the .NET binding stores
            // it as long/ulong, but Swift imports it as pointer-width Int/UInt.
            var isNative = attrs.Any(a => a.AttributeType.FullName == "ObjCRuntime.NativeAttribute");
            underlying = MapUnderlying(valueField?.FieldType, isNative);
            isFlags = attrs.Any(a => a.AttributeType.FullName == "System.FlagsAttribute");
            return AppleTypeSurfaceKind.Enum;
        }

        if (type.IsInterface)
            return AppleTypeSurfaceKind.Protocol;

        if (type.IsValueType)
            return AppleTypeSurfaceKind.Struct;

        // A C# static class (constants/helpers, e.g. UIWindowLevel's constant group) is
        // abstract + sealed; it has no instance shape and can't be a parameter/field type.
        if (type is { IsAbstract: true, IsSealed: true })
            return AppleTypeSurfaceKind.StaticConstants;

        return AppleTypeSurfaceKind.Class;
    }

    /// <summary>
    /// Maps a managed enum underlying type to the <em>Swift</em> raw-value spelling the enum
    /// marshalling expects — the wrapper reconstructs the Swift enum, so its raw value must match
    /// the Swift import, not the managed storage. A <c>[Native]</c> NS_ENUM/NS_OPTIONS stores as
    /// <c>long</c>/<c>ulong</c> in .NET but imports into Swift as pointer-width <c>Int</c>/<c>UInt</c>;
    /// a fixed-width C enum keeps its width (<c>Int32</c>, <c>UInt16</c>, …). Both spellings agree on
    /// byte width in <c>EnumHandler.GetCSharpEnumUnderlyingType</c> / <c>CdeclParamMapper</c>.
    /// </summary>
    private static string MapUnderlying(Type? underlying, bool isNative)
    {
        var name = underlying?.Name;
        var unsigned = name is "Byte" or "UInt16" or "UInt32" or "UInt64" or "UIntPtr";
        if (isNative)
            return unsigned ? "UInt" : "Int";

        return name switch
        {
            "SByte" => "Int8",
            "Byte" => "UInt8",
            "Int16" => "Int16",
            "UInt16" => "UInt16",
            "Int32" => "Int32",
            "UInt32" => "UInt32",
            "Int64" => "Int64",
            "UInt64" => "UInt64",
            "IntPtr" => "Int",
            "UIntPtr" => "UInt",
            _ => "Int",
        };
    }

    /// <summary>
    /// The Microsoft.* reference-pack token for a target platform. The pack directory
    /// (<c>{token}.Ref*</c>) and the ref assembly (<c>{token}.dll</c>) both derive from it — e.g.
    /// <c>Microsoft.iOS.Ref.net10.0_26.2/…/ref/net10.0/Microsoft.iOS.dll</c>. Casing matches the
    /// installed packs exactly (<c>macOS</c>/<c>tvOS</c>/<c>MacCatalyst</c>).
    /// </summary>
    internal static string RefPackName(ApplePlatform platform) => platform switch
    {
        ApplePlatform.iOS => "Microsoft.iOS",
        ApplePlatform.macOS => "Microsoft.macOS",
        ApplePlatform.tvOS => "Microsoft.tvOS",
        ApplePlatform.MacCatalyst => "Microsoft.MacCatalyst",
        _ => "Microsoft.iOS",
    };

    private static string? FindMicrosoftRefAssembly(ApplePlatform platform)
    {
        var token = RefPackName(platform);
        var packGlob = $"{token}.Ref*";
        var dllName = $"{token}.dll";

        foreach (var root in DotNetRoots())
        {
            var packsDir = Path.Combine(root, "packs");
            if (!Directory.Exists(packsDir))
                continue;

            var best = Directory.GetDirectories(packsDir, packGlob)
                .SelectMany(packDir => Directory.GetFiles(packDir, dllName, SearchOption.AllDirectories))
                .Where(IsRefAssemblyPath)
                .OrderByDescending(VersionFromRefPath)
                .FirstOrDefault();

            if (best is not null)
                return best;
        }

        return null;
    }

    private static string? FindNetCoreAppRefDir()
    {
        foreach (var root in DotNetRoots())
        {
            var packDir = Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref");
            if (!Directory.Exists(packDir))
                continue;

            var systemRuntime = Directory.GetFiles(packDir, "System.Runtime.dll", SearchOption.AllDirectories)
                .Where(IsRefAssemblyPath)
                .OrderByDescending(VersionFromRefPath)
                .FirstOrDefault();

            if (systemRuntime is not null)
                return Path.GetDirectoryName(systemRuntime);
        }

        return null;
    }

    private static bool IsRefAssemblyPath(string path)
        => path.Replace('\\', '/').Contains("/ref/", StringComparison.Ordinal);

    /// <summary>
    /// Extracts the pack version from a <c>{pack}/{version}/ref/{tfm}/{assembly}.dll</c> path so the
    /// newest installed pack wins. Returns 0.0 when the path doesn't match (sorts last).
    /// </summary>
    private static Version VersionFromRefPath(string file)
    {
        var versionDir = Path.GetDirectoryName(   // {pack}/{version}
            Path.GetDirectoryName(                 // {pack}/{version}/ref
                Path.GetDirectoryName(file)));      // {pack}/{version}/ref/{tfm}
        var segment = versionDir is null ? null : Path.GetFileName(versionDir);
        return Version.TryParse(segment, out var v) ? v : new Version(0, 0);
    }

    private static IEnumerable<string> DotNetRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The running shared framework lives at {root}/shared/Microsoft.NETCore.App/{ver}; climb to {root}.
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            var inferred = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
            if (seen.Add(inferred))
                yield return inferred;
        }

        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                     "/usr/local/share/dotnet",
                     "/usr/share/dotnet",
                     Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
                         ? Path.Combine(home, ".dotnet")
                         : null,
                 })
        {
            if (!string.IsNullOrEmpty(candidate) && seen.Add(candidate))
                yield return candidate;
        }
    }
}
