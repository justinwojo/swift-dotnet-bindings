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
/// Drift guard for the curated NSObject inherited-property set that
/// <see cref="NameProvider.ObjCRootedInheritedPropertyNames"/> feeds into the sibling-property rename
/// axis for ObjC-rooted bindings (Class 2 Bug A — a Swift method projecting to <c>Handle</c> shadows
/// the inherited <c>NSObject.Handle</c> property). The seeding set is hand-maintained so that
/// reproducible binding generation never depends on an installed Apple workload. These tests load the
/// installed Microsoft.iOS reference assembly via <see cref="MetadataLoadContext"/> (metadata only — no
/// code execution) and assert the curated set still mirrors the real <c>Foundation.NSObject</c> public
/// instance-property surface, so SDK drift (a new colliding property, or a removed one going stale)
/// fails loudly here rather than silently regressing the rename. The tests skip when the ref assembly
/// is not installed (e.g. CI without the iOS workload).
/// </summary>
public class ObjCRootedInheritedPropertyDriftTests
{
    [Fact]
    public void Seed_ObjCRootedClass_RenamesMethodCollidingWithInheritedProperty()
    {
        // The emitting plane and the conformance plane both shape this name. Seeded with the
        // inherited NSObject names, Swift `handle(url:)` must project to `HandleMethod` — it
        // cannot stay `Handle` without shadowing the inherited NSObject.Handle property.
        var propertyNames = new HashSet<string>();
        NameProvider.SeedObjCRootedInheritedPropertyNames(propertyNames, CreateClassDecl("SafariURLHandler", isObjCRooted: true));

        Assert.Contains("Handle", propertyNames);

        var shaped = NameProvider.GetPublicMethodName(
            "handle", isAsync: false, hasReturnValue: false, propertyNames: propertyNames, parameterCount: 1);

        Assert.Equal("HandleMethod", shaped);
    }

    [Fact]
    public void Seed_NonObjCRootedClass_LeavesMethodNameUnrenamed()
    {
        // Discrimination guard: a class that does not root in NSObject inherits none of those
        // properties, so the same Swift method must keep projecting to `Handle`. This is the
        // shape that is correct today (OAuthSwift's non-NSObject ExtensionContextURLHandler);
        // a seed that fired unconditionally would rename it and break its conformance instead.
        var propertyNames = new HashSet<string>();
        NameProvider.SeedObjCRootedInheritedPropertyNames(propertyNames, CreateClassDecl("ExtensionContextURLHandler", isObjCRooted: false));

        Assert.Empty(propertyNames);

        var shaped = NameProvider.GetPublicMethodName(
            "handle", isAsync: false, hasReturnValue: false, propertyNames: propertyNames, parameterCount: 1);

        Assert.Equal("Handle", shaped);
    }

    [Fact]
    public void Seed_NonClassDecl_IsLeftUnseeded()
    {
        // Structs and enums have no NSObject ancestry, so the conformance plane must not seed
        // inherited names for them — that would predict `HandleMethod` for a witness the emitter
        // still emits as `Handle` and drop a conformance that is valid today (CS0535's mirror
        // image: a silently missing interface).
        var propertyNames = new HashSet<string>();
        NameProvider.SeedObjCRootedInheritedPropertyNames(propertyNames, CreateStructDecl("PlainValue"));

        Assert.Empty(propertyNames);
    }

    private static StructDecl CreateStructDecl(string name)
    {
        return new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}VN",
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa",
            IsFrozen = false,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
    }

    private static ClassDecl CreateClassDecl(string name, bool isObjCRooted)
    {
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = null,
            ModuleDecl = null,
            IsObjCRooted = isObjCRooted,
        };
    }

    [SkippableFact]
    public void CuratedSet_CoversEveryPublicNSObjectInstanceProperty()
    {
        var reflected = LoadPublicNSObjectInstancePropertyNames();
        Skip.If(reflected is null, "Microsoft.iOS reference assembly not found; install the iOS workload to run this drift guard.");

        var curated = new HashSet<string>(NameProvider.ObjCRootedInheritedPropertyNames);
        var missing = reflected!.Where(p => !curated.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"Foundation.NSObject public instance properties not covered by NameProvider's curated set: " +
            $"{string.Join(", ", missing)}. A Swift method projecting to one of these names would shadow the " +
            $"inherited property on an ObjC-rooted binding — add them to _objCRootedInheritedPropertyNames.");
    }

    [SkippableFact]
    public void CuratedSet_HasNoStaleEntries()
    {
        var reflected = LoadPublicNSObjectInstancePropertyNames();
        Skip.If(reflected is null, "Microsoft.iOS reference assembly not found; install the iOS workload to run this drift guard.");

        var reflectedPublic = new HashSet<string>(reflected!);
        var allowedNonPublic = new HashSet<string>(NameProvider.ObjCRootedInheritedNonPublicPropertyNames);

        var stale = NameProvider.ObjCRootedInheritedPropertyNames
            .Where(c => !reflectedPublic.Contains(c) && !allowedNonPublic.Contains(c))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"Curated entries that are neither a public Foundation.NSObject instance property nor a documented " +
            $"non-public exception: {string.Join(", ", stale)}. Remove them, or (if intentionally non-public) add " +
            $"them to ObjCRootedInheritedNonPublicPropertyNames.");
    }

    [SkippableFact]
    public void Hash_IsDocumentedExclusion_NotAProperty()
    {
        var reflected = LoadPublicNSObjectInstancePropertyNames();
        Skip.If(reflected is null, "Microsoft.iOS reference assembly not found; install the iOS workload to run this drift guard.");

        // .NET surfaces NSObject's `hash` as the method GetNativeHash, not a property — so it must NOT be a
        // reflected property, and the curated set must NOT seed it (seeding would spuriously rename a Swift
        // hash() method). This pins the spec's documented exclusion.
        Assert.DoesNotContain("Hash", reflected!);
        Assert.DoesNotContain("Hash", NameProvider.ObjCRootedInheritedPropertyNames);
    }

    /// <summary>
    /// Loads the public instance-property names of <c>Foundation.NSObject</c> (declared + inherited) from
    /// the installed Microsoft.iOS reference assembly using a metadata-only <see cref="MetadataLoadContext"/>.
    /// Returns <c>null</c> when the ref assembly (or the matching core ref pack) is not installed.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reflection over an external metadata-only assembly in a test; no members of this app are trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Reflection over an external metadata-only assembly in a test; no members of this app are trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Reflection over an external metadata-only assembly in a test; no members of this app are trimmed.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MetadataLoadContext does not execute code; this test never runs under NativeAOT.")]
    private static HashSet<string>? LoadPublicNSObjectInstancePropertyNames()
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
        // The iOS ref pack ships type definitions for NSObject but pulls its core types (System.Object,
        // System.String, …) from the framework ref pack, so "System.Runtime" is the core assembly name.
        using var mlc = new MetadataLoadContext(resolver, "System.Runtime");
        var iosAsm = mlc.LoadFromAssemblyPath(iosRefAssembly);
        var nsObject = iosAsm.GetType("Foundation.NSObject");
        if (nsObject is null)
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (Type? t = nsObject; t is not null; t = t.BaseType)
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length > 0)
                    continue; // indexers project to C# indexers, not named members — they can't shadow by name
                names.Add(p.Name);
            }
        }

        return names;
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
