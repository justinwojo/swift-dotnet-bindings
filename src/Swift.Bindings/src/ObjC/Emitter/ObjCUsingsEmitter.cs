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

    /// <summary>
    /// Emits the ApiDefinition.cs <c>using</c> header: the curated <see cref="ApiDefinitionUsings"/>
    /// baseline (in its original order, so existing generated output stays churn-free) followed by any
    /// <paramref name="referencedAppleNamespaces"/> not already in the baseline, appended in sorted
    /// order. The baseline carries the namespaces of Apple value types (CGRect, CLLocationCoordinate2D)
    /// that resolve through the type mapper's known-types set and therefore have no AST provenance; the
    /// appended set carries the namespaces of referenced Apple SDK classes/protocols derived from
    /// authoritative header provenance, so a framework outside the baseline (e.g. StoreKit) resolves
    /// without the list ever being hand-edited. The appended namespaces come from ground-truth
    /// <c>.framework</c> provenance rather than a hand-maintained list, so the startup
    /// <see cref="ReferencedAppleFrameworkModules"/> registry assertion (which guards the baseline
    /// against typos/staleness) does not apply to them.
    /// </summary>
    public static void EmitApiDefinitionHeader(
        StringBuilder sb, PlatformInfo? platformInfo, IReadOnlySet<string> referencedAppleNamespaces)
    {
        EmitFiltered(sb, ApiDefinitionUsings, platformInfo);

        var baseline = new HashSet<string>(ApiDefinitionUsings, StringComparer.Ordinal);
        foreach (var ns in referencedAppleNamespaces
                     .Where(ns => !baseline.Contains(ns))
                     .OrderBy(ns => ns, StringComparer.Ordinal))
        {
            if (IsAvailable(ns, platformInfo))
                sb.AppendLine($"using {ns};");
        }
    }

    /// <summary>
    /// Collects the owning .NET namespaces of the Apple SDK class/protocol types referenced anywhere
    /// in <paramref name="module"/>'s ApiDefinition surface (superclasses, conformances, property and
    /// method signatures), using the parser's name→namespace provenance map. The walk is intentionally
    /// a superset of what actually emits — it ignores per-member resolvability gating, because emitting
    /// a <c>using</c> for a referenced-but-skipped type is harmless (an unused using is not an error)
    /// while omitting one a member needs is a CS0246. Matching is raw-ObjC-name to raw-ObjC-name
    /// (the map keys and the model type names are both pre-mapping ObjC identifiers), so it needs none
    /// of the acronym-reversal the resolvability gate performs against mapped C# names. Returns an empty
    /// set when no provenance is available (e.g. -fmodules mode).
    /// </summary>
    internal static IReadOnlySet<string> CollectReferencedApiDefinitionNamespaces(
        ObjCModule module, IReadOnlyDictionary<string, string>? appleSdkTypeNamespaces)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (appleSdkTypeNamespaces is null || appleSdkTypeNamespaces.Count == 0)
            return result;

        void RecordName(string? rawName)
        {
            if (rawName != null
                && appleSdkTypeNamespaces.TryGetValue(rawName, out var ns)
                && ns.Length > 0)
                result.Add(ns);
        }

        void RecordType(ObjCTypeRef? type)
        {
            if (type is null) return;
            RecordName(type.Name);
            foreach (var q in type.ProtocolQualifications) RecordName(q);
            RecordType(type.PointeeType);
            RecordType(type.BlockReturnType);
            foreach (var bp in type.BlockParams) RecordType(bp);
            foreach (var ga in type.GenericArgs) RecordType(ga);
        }

        void RecordMethod(ObjCMethodDecl method)
        {
            RecordType(method.ReturnType);
            foreach (var p in method.Parameters) RecordType(p.Type);
        }

        foreach (var cls in module.Classes)
        {
            RecordName(cls.SuperclassName);
            foreach (var p in cls.ProtocolNames) RecordName(p);
            foreach (var prop in cls.Properties) RecordType(prop.Type);
            foreach (var m in cls.Methods) RecordMethod(m);
        }

        foreach (var proto in module.Protocols)
        {
            foreach (var p in proto.InheritedProtocolNames) RecordName(p);
            foreach (var prop in proto.Properties) RecordType(prop.Type);
            foreach (var m in proto.Methods) RecordMethod(m);
        }

        foreach (var cat in module.Categories)
        {
            RecordName(cat.ClassName);
            foreach (var p in cat.ProtocolNames) RecordName(p);
            foreach (var prop in cat.Properties) RecordType(prop.Type);
            foreach (var m in cat.Methods) RecordMethod(m);
        }

        // Free functions and constants too: even where they don't surface in ApiDefinition.cs the
        // collection is a deliberate superset (an extra unused `using` is harmless; a missing one is
        // a CS0246), and it guards against any emit path that does reference an Apple type by name.
        foreach (var fn in module.Functions)
        {
            RecordType(fn.ReturnType);
            foreach (var p in fn.Parameters) RecordType(p.Type);
        }
        foreach (var c in module.Constants)
            RecordType(c.Type);

        return result;
    }

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
