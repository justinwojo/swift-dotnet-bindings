// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
/// A name→shape index of the public Microsoft.iOS binding surface, built once by reading the
/// installed reference assembly with a metadata-only <see cref="MetadataLoadContext"/>.
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
/// The reference assemblies live on disk in the installed iOS workload; the index is built at most
/// once per process (a <see cref="Lazy{T}"/> singleton). When the workload isn't present the
/// singleton is null and callers fall back to name synthesis, so generation never depends on it.
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

    private static readonly Lazy<AppleTypeSurfaceIndex?> s_default =
        new(BuildFromInstalledRefPack, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The process-wide index, or null when the Microsoft.iOS reference assembly isn't installed.</summary>
    internal static AppleTypeSurfaceIndex? Default => s_default.Value;

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MetadataLoadContext reads metadata only and never executes code; the generator never runs under NativeAOT.")]
    private static AppleTypeSurfaceIndex? BuildFromInstalledRefPack()
    {
        try
        {
            var iosRefAssembly = FindMicrosoftIosRefAssembly();
            if (iosRefAssembly is null)
                return null;

            var coreRefDir = FindNetCoreAppRefDir();
            if (coreRefDir is null)
                return null;

            var refDir = Path.GetDirectoryName(iosRefAssembly)!;
            var assemblies = Directory.GetFiles(refDir, "*.dll")
                .Concat(Directory.GetFiles(coreRefDir, "*.dll"))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();

            var resolver = new PathAssemblyResolver(assemblies);
            // The iOS ref pack defines its own types but pulls core types (System.Object, …) from the
            // framework ref pack, whose runtime-facing assembly name is "System.Runtime".
            using var mlc = new MetadataLoadContext(resolver, "System.Runtime");
            var iosAsm = mlc.LoadFromAssemblyPath(iosRefAssembly);
            return BuildFromAssembly(iosAsm);
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

    private static string? FindMicrosoftIosRefAssembly()
    {
        foreach (var root in DotNetRoots())
        {
            var packsDir = Path.Combine(root, "packs");
            if (!Directory.Exists(packsDir))
                continue;

            var best = Directory.GetDirectories(packsDir, "Microsoft.iOS.Ref*")
                .SelectMany(packDir => Directory.GetFiles(packDir, "Microsoft.iOS.dll", SearchOption.AllDirectories))
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
