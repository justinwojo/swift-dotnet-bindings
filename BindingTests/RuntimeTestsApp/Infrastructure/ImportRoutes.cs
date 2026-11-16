// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Reads the route a generated member takes from its imports. The annotation on the declaring type
/// keeps its non-public methods through trimming, which the reflection needs under NativeAOT.
/// </summary>
internal static class ImportRoutes
{
    /// <summary>
    /// Whether <paramref name="import"/> is called with the C convention. The generator writes no
    /// <see cref="UnmanagedCallConvAttribute"/> for it, since C is the platform default on every Apple
    /// target, so an import with none is a C import; one that does name a convention must name only C.
    /// </summary>
    public static bool UsesCConvention(MethodInfo import)
    {
        var convs = import.GetCustomAttribute<UnmanagedCallConvAttribute>()?.CallConvs ?? Array.Empty<Type>();
        return convs.Length == 0 || (convs.Length == 1 && convs[0] == typeof(CallConvCdecl));
    }

    /// <summary>
    /// Whether this platform's regeneration routed Swift-convention imports through native bridges:
    /// the iOS, Mac Catalyst and tvOS builds, whose swiftc lowering evidence the qualification record
    /// holds. macOS regenerates without evidence, so a member a bridge would carry keeps its
    /// <c>@_cdecl</c> wrapper or its guarded direct import there.
    /// </summary>
    public static bool BridgesRouted => OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS();

    /// <summary>
    /// Asserts that <paramref name="member"/> of <paramref name="declaringType"/> has exactly one import,
    /// and that it enters a <c>@_cdecl</c> wrapper: a wrapper symbol, the C convention only, no native
    /// bridge twin and no Swift self register.
    /// </summary>
    public static void AssertCdeclWrapper(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)] Type declaringType,
        string member)
    {
        var imports = declaringType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("PInvoke_" + member + "_", StringComparison.Ordinal)
                && m.GetCustomAttribute<LibraryImportAttribute>() != null)
            .ToArray();
        var described = string.Join(", ", imports.Select(m =>
            $"{m.Name} -> {m.GetCustomAttribute<LibraryImportAttribute>()!.EntryPoint}"));
        TestLogger.Info($"{declaringType.Name}.{member}: {described}");

        var name = $"{declaringType.Name}.{member}";
        if (imports.Length != 1)
            throw new AssertionException($"{name} has exactly one import: Expected 1, got {imports.Length}");
        var import = imports[0];
        var entryPoint = import.GetCustomAttribute<LibraryImportAttribute>()!.EntryPoint ?? import.Name;
        if (!entryPoint.StartsWith("SBW_", StringComparison.Ordinal))
            throw new AssertionException($"{name} enters through a @_cdecl wrapper symbol (got {entryPoint})");
        if (!UsesCConvention(import))
            throw new AssertionException($"{name} is declared with the C convention only");
        if (import.Name.Contains("__SBSR_", StringComparison.Ordinal))
            throw new AssertionException($"{name} is not a native bridge twin");
        if (import.GetParameters().Any(p => p.ParameterType == typeof(SwiftSelf)))
            throw new AssertionException($"{name} carries no Swift self register");
    }

    /// <summary>
    /// Reads the route the imports of <paramref name="member"/> (its <c>PInvoke_{member}_</c> methods,
    /// so <c>init</c>, a Swift method name, or a name with its hash) take into Swift: a native bridge
    /// twin, a native thunk, a <c>@_cdecl</c> wrapper, or a direct Swift-convention import. Every import
    /// of the member must agree, so a member that split across routes fails here rather than letting a
    /// test pass on whichever import it happened to exercise. The route is logged.
    /// </summary>
    public static ImportRoute RouteOf(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)] Type declaringType,
        string member)
        => RouteOf(declaringType, member, declaringType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly));

    /// <summary>
    /// <see cref="RouteOf(Type, string)"/> for a declaring type too large to keep whole through
    /// trimming (the module's free functions). The caller roots the imports it reads with a
    /// <see cref="DynamicDependencyAttribute"/> naming each one, so this reads only those.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Callers root each import they read with DynamicDependency.")]
    public static ImportRoute RouteOfRooted(Type declaringType, string member)
        => RouteOf(declaringType, member, declaringType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly));

    private static ImportRoute RouteOf(Type declaringType, string member, MethodInfo[] methods)
    {
        var name = $"{declaringType.Name}.{member}";
        var candidates = methods
            .Where(m => m.Name.StartsWith("PInvoke_" + member + "_", StringComparison.Ordinal))
            .ToArray();
        var imports = candidates.Where(m => m.GetCustomAttribute<LibraryImportAttribute>() != null).ToArray();
        var routes = imports.Select(Classify).Distinct().ToArray();
        TestLogger.Info($"route {name}: {string.Join(", ", routes)} via " +
            string.Join(", ", imports.Select(m => $"{m.Name} -> {m.GetCustomAttribute<LibraryImportAttribute>()!.EntryPoint}")));
        if (imports.Length == 0)
            throw new AssertionException($"{name} has no generated import (found {candidates.Length} candidate methods)");
        if (routes.Length != 1)
            throw new AssertionException($"{name} reaches Swift through one route, got {string.Join(", ", routes)}");
        return routes[0];
    }

    /// <summary>Asserts that <paramref name="member"/> takes <paramref name="expected"/> and logs it.</summary>
    public static void AssertRoute(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)] Type declaringType,
        string member,
        ImportRoute expected)
    {
        var actual = RouteOf(declaringType, member);
        if (actual != expected)
            throw new AssertionException($"{declaringType.Name}.{member} takes the {expected} route: Expected {expected}, got {actual}");
    }

    /// <summary>
    /// The route a member that a bridge carries where the platform has lowering evidence takes:
    /// <see cref="ImportRoute.Bridge"/> on iOS, Mac Catalyst and tvOS, otherwise
    /// <paramref name="withoutEvidence"/>.
    /// </summary>
    public static ImportRoute Bridged(ImportRoute withoutEvidence) => BridgesRouted ? ImportRoute.Bridge : withoutEvidence;

    private static ImportRoute Classify(MethodInfo import)
    {
        var entryPoint = import.GetCustomAttribute<LibraryImportAttribute>()!.EntryPoint ?? import.Name;
        if (import.Name.Contains("__SBSR_", StringComparison.Ordinal) || entryPoint.StartsWith("SBSR_", StringComparison.Ordinal))
            return ImportRoute.Bridge;
        if (entryPoint.StartsWith("thunk_", StringComparison.Ordinal))
            return ImportRoute.Thunk;
        var convs = import.GetCustomAttribute<UnmanagedCallConvAttribute>()?.CallConvs ?? Array.Empty<Type>();
        if (convs.Contains(typeof(CallConvSwift)))
            return ImportRoute.Direct;
        if (entryPoint.StartsWith("SBW_", StringComparison.Ordinal) && UsesCConvention(import))
            return ImportRoute.Cdecl;
        return ImportRoute.Other;
    }
}

/// <summary>The route a generated import takes into Swift.</summary>
internal enum ImportRoute
{
    /// <summary>A C-convention twin into a native bridge stub (<c>SBSR_</c>) that loads the Swift registers.</summary>
    Bridge,
    /// <summary>A hand-assembled native thunk (<c>thunk_</c>).</summary>
    Thunk,
    /// <summary>A generated <c>@_cdecl</c> wrapper (<c>SBW_</c>).</summary>
    Cdecl,
    /// <summary>A direct Swift-convention import against the member's own symbol.</summary>
    Direct,
    /// <summary>Anything else: a C import that is none of the above.</summary>
    Other,
}
