// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Drift guard for <see cref="ObjCInheritedMemberSurface"/>, the hand-kept table of the C# members
/// an Objective-C-rooted binding inherits from <c>Foundation.NSObject</c>. The table decides whether
/// a Swift override of <c>description</c>, <c>didChangeValue(forKey:)</c> and the like emits
/// <c>override</c> (and so reaches Swift from every <c>NSObject</c>-typed caller) or is hidden behind
/// a pragma. These tests rebuild the same surface from each installed Apple platform's reference
/// assembly through a metadata-only <see cref="MetadataLoadContext"/>: the common tables must hold
/// exactly the members every installed platform declares with the same shape, and every other
/// member name must be listed as platform-specific. They skip when no Apple ref pack is installed.
/// </summary>
public class ObjCInheritedMemberSurfaceDriftTests
{
    private static readonly string[] PlatformAssemblies =
        { "Microsoft.iOS", "Microsoft.tvOS", "Microsoft.MacCatalyst", "Microsoft.macOS" };

    [SkippableFact]
    public void CommonTables_MatchEveryInstalledPlatform()
    {
        var surfaces = LoadSurfaces();
        Skip.If(surfaces.Count == 0, "No Apple platform reference assembly installed.");

        var tableProperties = ObjCInheritedMemberSurface.Properties
            .Select(p => PropertyKey(p.Name, p.Type, p.HasSetter, p.IsVirtual)).ToHashSet(StringComparer.Ordinal);
        var tableMethods = ObjCInheritedMemberSurface.Methods
            .Select(m => MethodKey(m.Name, m.ParameterTypes, m.ReturnType, m.IsVirtual)).ToHashSet(StringComparer.Ordinal);
        var workloadSpecificMethods = ObjCInheritedMemberSurface.WorkloadSpecificMethods
            .Select(w => MethodKey(w.Method.Name, w.Method.ParameterTypes, w.Method.ReturnType, w.Method.IsVirtual))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (platform, surface) in surfaces)
        {
            var missingProperties = tableProperties.Except(surface.Properties).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var missingMethods = tableMethods.Except(surface.Methods).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(missingProperties.Count == 0 && missingMethods.Count == 0,
                $"{platform}'s NSObject no longer declares these table entries with this shape: " +
                $"{string.Join("; ", missingProperties.Concat(missingMethods))}.");
        }

        if (surfaces.Count == PlatformAssemblies.Length)
        {
            var commonProperties = surfaces.Values.Select(s => s.Properties).Aggregate((a, b) => a.Intersect(b).ToHashSet());
            var commonMethods = surfaces.Values.Select(s => s.Methods).Aggregate((a, b) => a.Intersect(b).ToHashSet());
            var unlisted = commonProperties.Except(tableProperties)
                .Concat(commonMethods.Except(tableMethods).Except(workloadSpecificMethods))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            Assert.True(unlisted.Count == 0,
                $"Members every platform's NSObject declares that the table lacks: {string.Join("; ", unlisted)}.");
        }
    }

    /// <summary>
    /// A workload-specific method is declared, with exactly the table's shape, on every platform of a
    /// workload at or after its first major version and on none before it, so the entry tracks the
    /// installed workload whichever supported one it is. It must never also sit in the common table,
    /// which feeds an <c>override</c> no workload before it could compile.
    /// </summary>
    [SkippableFact]
    public void WorkloadSpecificMethods_MatchTheInstalledWorkloadVersion()
    {
        var surfaces = LoadSurfaces();
        Skip.If(surfaces.Count == 0, "No Apple platform reference assembly installed.");

        var tableMethods = ObjCInheritedMemberSurface.Methods
            .Select(m => MethodKey(m.Name, m.ParameterTypes, m.ReturnType, m.IsVirtual)).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in ObjCInheritedMemberSurface.WorkloadSpecificMethods)
        {
            var key = MethodKey(entry.Method.Name, entry.Method.ParameterTypes, entry.Method.ReturnType, entry.Method.IsVirtual);
            Assert.DoesNotContain(key, tableMethods);
            foreach (var (platform, surface) in surfaces)
            {
                var expected = surface.WorkloadVersion.Major >= entry.FirstWorkloadMajor;
                Assert.True(surface.Methods.Contains(key) == expected,
                    $"{platform} workload {surface.WorkloadVersion}: expected '{key}' to be " +
                    $"{(expected ? "declared" : "absent")} (first workload major {entry.FirstWorkloadMajor}).");
            }
        }
    }

    [Theory]
    [InlineData("ObjCRuntime.NativeHandle", "string", "bool")]
    [InlineData("global::ObjCRuntime.NativeHandle", "global::System.String", "System.Boolean")]
    public void ClassifyMethod_WorkloadSpecificSignature_Hides(string handle, string name, string flag)
    {
        // Hides under the workloads that declare it and hides nothing under the others; only the
        // suppressed declaration compiles under both, so the classification is Hide even for an override.
        Assert.Equal(ObjCInheritedMemberSurface.Disposition.Hide,
            ObjCInheritedMemberSurface.ClassifyMethod("InitializeHandle", new[] { handle, name, flag }, "void", isSwiftOverride: false));
        Assert.Equal(ObjCInheritedMemberSurface.Disposition.Hide,
            ObjCInheritedMemberSurface.ClassifyMethod("InitializeHandle", new[] { handle, name, flag }, "void", isSwiftOverride: true));
        // A different overload of the same name is an overload, not a hide.
        Assert.Equal(ObjCInheritedMemberSurface.Disposition.None,
            ObjCInheritedMemberSurface.ClassifyMethod("InitializeHandle", new[] { handle, name, flag, "int" }, "void", isSwiftOverride: false));
    }

    [SkippableFact]
    public void EveryInstalledMemberName_IsCommonOrPlatformSpecific()
    {
        var surfaces = LoadSurfaces();
        Skip.If(surfaces.Count == 0, "No Apple platform reference assembly installed.");

        var known = ObjCInheritedMemberSurface.Properties.Select(p => p.Name)
            .Concat(ObjCInheritedMemberSurface.Methods.Select(m => m.Name))
            .Concat(ObjCInheritedMemberSurface.PlatformSpecificMemberNames)
            .Concat(ObjCInheritedMemberSurface.WorkloadSpecificMethods.Select(e => e.Method.Name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (platform, surface) in surfaces)
        {
            var unknown = surface.Names.Where(n => !known.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.True(unknown.Count == 0,
                $"{platform}'s NSObject declares members the binding does not know: {string.Join(", ", unknown)}. " +
                "Add them to ObjCInheritedMemberSurface's common tables or to PlatformSpecificMemberNames.");
        }
    }

    [SkippableFact]
    public void PlatformSpecificNames_AreDeclaredSomewhereButNotEverywhere()
    {
        var surfaces = LoadSurfaces();
        Skip.If(surfaces.Count < PlatformAssemblies.Length, "Needs every Apple platform reference assembly.");

        foreach (var name in ObjCInheritedMemberSurface.PlatformSpecificMemberNames)
        {
            var declaring = surfaces.Count(s => s.Value.Names.Contains(name));
            Assert.True(declaring > 0 && declaring < surfaces.Count,
                $"'{name}' is listed as platform-specific but {declaring} of {surfaces.Count} platforms declare it.");
        }
    }

    private static string PropertyKey(string name, string type, bool hasSetter, bool isVirtual)
        => $"P {name}: {ObjCInheritedMemberSurface.NormalizeType(type)} set={hasSetter} virtual={isVirtual}";

    private static string MethodKey(string name, IEnumerable<string> parameterTypes, string returnType, bool isVirtual)
        => $"M {name}({string.Join(",", parameterTypes.Select(ObjCInheritedMemberSurface.NormalizeType))}): " +
           $"{ObjCInheritedMemberSurface.NormalizeType(returnType)} virtual={isVirtual}";

    private sealed record Surface(HashSet<string> Properties, HashSet<string> Methods, HashSet<string> Names, Version WorkloadVersion);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Metadata-only reflection in a test.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Metadata-only reflection in a test.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Metadata-only reflection in a test.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "MetadataLoadContext does not execute code.")]
    private static Dictionary<string, Surface> LoadSurfaces()
    {
        var result = new Dictionary<string, Surface>(StringComparer.Ordinal);
        var coreRefDir = ObjCRootedInheritedPropertyDriftTests.FindNetCoreAppRefDir();
        if (coreRefDir is null)
            return result;

        foreach (var platform in PlatformAssemblies)
        {
            var refAssembly = FindRefAssembly(platform);
            if (refAssembly is null)
                continue;

            var assemblies = Directory.GetFiles(Path.GetDirectoryName(refAssembly)!, "*.dll")
                .Concat(Directory.GetFiles(coreRefDir, "*.dll"))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();
            using var mlc = new MetadataLoadContext(new PathAssemblyResolver(assemblies), "System.Runtime");
            var nsObject = mlc.LoadFromAssemblyPath(refAssembly).GetType("Foundation.NSObject");
            if (nsObject is null)
                continue;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var properties = new HashSet<string>(StringComparer.Ordinal);
            var methods = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in nsObject.GetProperties(flags))
            {
                if (p.GetMethod is not { } getter || !IsVisibleToSubclass(getter) || p.GetIndexParameters().Length > 0)
                    continue;
                var hasSetter = p.SetMethod is { } setter && IsVisibleToSubclass(setter);
                properties.Add(PropertyKey(p.Name, Format(p.PropertyType), hasSetter, getter.IsVirtual && !getter.IsFinal));
                names.Add(p.Name);
            }
            foreach (var m in nsObject.GetMethods(flags))
            {
                if (m.IsSpecialName || !IsVisibleToSubclass(m))
                    continue;
                methods.Add(MethodKey(m.Name, m.GetParameters().Select(x => Format(x.ParameterType)), Format(m.ReturnType), m.IsVirtual && !m.IsFinal));
                names.Add(m.Name);
            }
            // The prerelease-aware parse: a "27.0.x-rc.2" pack folder is workload 27, not 0.0.
            result[platform] = new Surface(properties, methods, names,
                CSharpProbeReferenceSet.VersionFromRefPath(refAssembly));
        }
        return result;
    }

    private static bool IsVisibleToSubclass(MethodBase m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;

    // The table's spelling: namespace-qualified names, C# generic syntax, keyword primitives
    // (the keyword mapping itself is ObjCInheritedMemberSurface.NormalizeType's).
    private static string Format(Type type)
    {
        if (type.IsArray)
            return Format(type.GetElementType()!) + "[]";
        if (type.IsGenericType)
        {
            var name = type.GetGenericTypeDefinition().FullName!;
            return $"{name[..name.IndexOf('`')]}<{string.Join(",", type.GetGenericArguments().Select(Format))}>";
        }
        return type.FullName!;
    }

    private static string? FindRefAssembly(string assemblyName)
    {
        foreach (var root in ObjCRootedInheritedPropertyDriftTests.DotNetRoots())
        {
            var packsDir = Path.Combine(root, "packs");
            if (!Directory.Exists(packsDir))
                continue;
            var best = Directory.GetDirectories(packsDir, $"{assemblyName}.Ref*")
                .SelectMany(packDir => Directory.GetFiles(packDir, $"{assemblyName}.dll", SearchOption.AllDirectories))
                .Where(ObjCRootedInheritedPropertyDriftTests.IsRefAssemblyPath)
                .OrderByDescending(CSharpProbeReferenceSet.VersionFromRefPath)
                .FirstOrDefault();
            if (best is not null)
                return best;
        }
        return null;
    }
}
