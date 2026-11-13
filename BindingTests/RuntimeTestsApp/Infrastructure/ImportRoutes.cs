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
}
