// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration.ObjC;

/// <summary>
/// Centralises the kitchen-sink <c>using</c> header bgen needs to resolve
/// type references in generated C# binding files (<c>ApiDefinition.cs</c>,
/// <c>StructsAndEnums.cs</c>, <c>BgenDelegates.cs</c>), and filters that
/// header against <see cref="AppleFrameworkRegistry.IsModuleAvailableOnPlatform"/>
/// so namespaces unavailable on the target TFM are dropped at emit time.
///
/// Concrete motivation: <c>using UIKit;</c> on <c>net10.0-macos</c> caused
/// CS0246 on every generated ObjC binding even when no UIKit type was
/// actually referenced. The generator runs once per inner-build TFM, so
/// the right shape is to filter per-platform here rather than wrap each
/// using in a <c>#if</c> at the file level.
/// </summary>
internal static class ObjCUsingsEmitter
{
    // Namespaces that are NOT Apple-framework modules and therefore must
    // bypass AppleFrameworkRegistry's availability gate (the registry
    // tracks Apple modules — System.* and ObjCRuntime aren't in it).
    private static readonly HashSet<string> AlwaysAvailable = new(StringComparer.Ordinal)
    {
        "System",
        "System.Runtime.InteropServices",
        "ObjCRuntime",
    };

    // Kitchen-sink set bgen consults when resolving member-level type
    // references in ApiDefinition.cs. Order is the original emit order
    // so generated diffs stay churn-free.
    private static readonly string[] ApiDefinitionUsings =
    [
        "System",
        "AuthenticationServices",
        "AVFoundation",
        "BackgroundAssets",
        "CoreAnimation",
        "CoreFoundation",
        "CoreImage",
        "CoreLocation",
        "CoreMedia",
        "Foundation",
        "ImageIO",
        "MapKit",
        "Metal",
        "ObjCRuntime",
        "OpenGLES",
        "CoreGraphics",
        "UIKit",
        "UserNotifications",
        "WebKit",
    ];

    private static readonly string[] StructsAndEnumsUsings =
    [
        "System",
        "System.Runtime.InteropServices",
        "CoreAnimation",
        "CoreFoundation",
        "CoreGraphics",
        "CoreLocation",
        "CoreMedia",
        "Foundation",
        "ObjCRuntime",
        "UIKit",
    ];

    private static readonly string[] BgenDelegatesUsings =
    [
        "System",
        "Foundation",
        "ObjCRuntime",
        "UIKit",
        "CoreGraphics",
        "CoreLocation",
        "CoreMedia",
    ];

    /// <summary>
    /// The distinct Apple-framework module names referenced by the <c>using</c> headers this
    /// emitter writes (excluding the non-Apple <see cref="AlwaysAvailable"/> namespaces). A
    /// framework is "handled" by the generator either by being type-bridged (AutoBridge /
    /// OptionalFallback in apple-frameworks.json) OR by being referenced here so its <c>using</c>
    /// resolves platform types the binding emits by name (e.g. <c>EAGLContext</c> from OpenGLES).
    /// Both are legitimate reasons to carry a platform-availability annotation in the registry,
    /// so this set is the second half of that "is this a framework we actually touch?" oracle.
    /// </summary>
    internal static IReadOnlySet<string> ReferencedAppleFrameworkModules { get; } =
        ApiDefinitionUsings
            .Concat(StructsAndEnumsUsings)
            .Concat(BgenDelegatesUsings)
            .Where(name => !AlwaysAvailable.Contains(name))
            .ToHashSet(StringComparer.Ordinal);

    static ObjCUsingsEmitter()
    {
        // Defense in depth: assert at process startup that every Apple-framework name
        // we might emit is catalogued in apple-frameworks.json. Without this, a typo
        // or a new iOS-only namespace added to the lists below would slip past the
        // IsModuleAvailableOnPlatform gate (which conservatively returns true for
        // unknown modules) and silently re-introduce the original cross-TFM CS0246.
        var unknown = ReferencedAppleFrameworkModules
            .Where(name => !AppleFrameworkRegistry.IsKnownModule(name))
            .ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException(
                $"ObjCUsingsEmitter references Apple-framework module(s) missing from apple-frameworks.json: "
                + $"{string.Join(", ", unknown)}. Add registry entries so the platform-availability filter can gate them.");
    }

    public static void EmitApiDefinitionHeader(StringBuilder sb, PlatformInfo? platformInfo)
        => EmitFiltered(sb, ApiDefinitionUsings, platformInfo);

    public static void EmitStructsAndEnumsHeader(StringBuilder sb, PlatformInfo? platformInfo)
        => EmitFiltered(sb, StructsAndEnumsUsings, platformInfo);

    public static void EmitBgenDelegatesHeader(StringBuilder sb, PlatformInfo? platformInfo)
        => EmitFiltered(sb, BgenDelegatesUsings, platformInfo);

    private static void EmitFiltered(StringBuilder sb, string[] usings, PlatformInfo? platformInfo)
    {
        foreach (var ns in usings)
        {
            if (IsAvailable(ns, platformInfo))
                sb.AppendLine($"using {ns};");
        }
    }

    private static bool IsAvailable(string ns, PlatformInfo? platformInfo)
    {
        if (AlwaysAvailable.Contains(ns))
            return true;
        return AppleFrameworkRegistry.IsModuleAvailableOnPlatform(ns, platformInfo?.Platform);
    }
}
