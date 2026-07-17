// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Detects dependency-module / public-type name collisions: a `--framework-dependency`
/// whose module name is also the name of a public Swift type (class/struct/enum/actor/
/// protocol) or Objective-C class that the dependency exports.
///
/// Why this matters: when a bound module's swiftinterface references qualified names
/// like <c>GTMSessionFetcher.GTMSessionFetcherServiceProtocol</c>, swiftc tries to
/// resolve the leading identifier as either the <i>module</i> <c>GTMSessionFetcher</c>
/// or the <i>class</i> <c>GTMSessionFetcher</c>. The class wins, member lookup fails,
/// and swiftc reports "type 'GTMSessionFetcher' has no member 'GTMSessionFetcherServiceProtocol'".
/// <see cref="SwiftWrapperCompiler.PrecompileCollidingModule"/> already knows how to
/// patch the bound interface to strip the <c>&lt;Module&gt;.</c> qualifier; this detector
/// is the missing piece that finds <i>which</i> dep modules need the patch.
///
/// <para>
/// Two detection paths cover both Swift and ObjC-only dependencies:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Swift dep with swiftinterface</b>: regex over the <c>.private.swiftinterface</c>
///     (or <c>.swiftinterface</c>) for <c>public class|struct|enum|actor|protocol|typealias &lt;ModuleName&gt;</c>.
///   </description></item>
///   <item><description>
///     <b>ObjC-only dep</b>: regex over the umbrella header <c>&lt;ModuleName&gt;.h</c>
///     (or all <c>*.h</c> files if the umbrella has no match) for
///     <c>@interface &lt;ModuleName&gt;</c>. Excludes <c>@class &lt;ModuleName&gt;;</c>
///     forward declarations. <c>@interface &lt;ModuleName&gt; (Category)</c> matches
///     intentionally — a category still makes Swift import a class named
///     <c>&lt;ModuleName&gt;</c>, so the collision risk is real.
///   </description></item>
/// </list>
///
/// <para>
/// Precedent: <see cref="SwiftWrapperCompiler"/> already calls
/// <see cref="SwiftWrapperCompiler.PrecompileCollidingModule"/> for the XCTest case
/// when <c>DetectXCTestDependency</c> matches. This detector generalizes that pattern
/// to any dep module whose name collides with one of its own exports.
/// </para>
/// </summary>
public static class DepModuleCollisionDetector
{
    // ^\s* anchors at line start (multiline). Permit any number of attributes, then a
    // run of declaration modifiers on EITHER side of the access keyword. swiftc emits
    // the modifier before `public` for several type kinds — `final public class`,
    // `indirect public enum`, `nonisolated public class` — as well as after it
    // (`public final class`). A `final`-only allowance silently missed `indirect`/
    // `nonisolated`, so the qualifier-strip never fired and the bound wrapper failed
    // swiftc with "type '<Module>' has no member …". `nonisolated` may carry a paren
    // argument (`nonisolated(unsafe)`), so tolerate an optional `(…)`. Bind the captured
    // module name with a trailing word-boundary — `public class FooBar` must not match
    // for module `Foo`. (`dynamic` is a member-only modifier swiftc rejects on a type
    // declaration, so it can never appear here — see Regression-R6 finding 5.)
    //
    // `typealias` is in the alternation because an alias shadows the module name exactly as a
    // nominal type does: it introduces the name into type scope, so Swift resolves the leading
    // identifier of `<Name>.X` to the alias and looks for X inside the aliased type instead of
    // the module. A module-level `public typealias Foo = SomeOtherType` in module `Foo` is a
    // real shape (a deprecated shorthand kept for source compatibility is the usual reason).
    // The modifier runs are shared with the nominal kinds and simply never match an alias —
    // swiftc rejects `final`/`indirect`/`nonisolated` on a typealias — so the union is safe.
    private const string TypeDeclModifiers = @"(?:(?:final|indirect|nonisolated)(?:\s*\([^)]*\))?\s+)*";
    private static readonly Regex SwiftPublicTypeRegex = new(
        @"^\s*(?:@[A-Za-z_][A-Za-z0-9_]*(?:\([^)]*\))?\s+)*" + TypeDeclModifiers + @"(?:public|open)\s+" + TypeDeclModifiers + @"(?:class|struct|enum|actor|protocol|typealias)\s+([A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // ^\s* anchors at line start. Match `@interface Foo` and `@interface Foo (Cat)`,
    // but not `@class Foo;` (forward decl). The regex also doesn't fire inside
    // /* ... */ block comments because comment lines start with `*` or whitespace
    // before `*`. Inline `//` comments after `@interface Foo` don't affect us
    // because we only check the leading match.
    private static readonly Regex ObjCInterfaceRegex = new(
        @"^\s*@interface\s+([A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Per-slice collision result. Each list holds the dep module names that collide
    /// with their own public-type / ObjC class export <i>within the named slice</i>.
    /// </summary>
    public readonly record struct SlicedCollisionResult(
        IReadOnlyList<string> Simulator,
        IReadOnlyList<string> Device)
    {
        public bool IsEmpty => Simulator.Count == 0 && Device.Count == 0;
    }

    /// <summary>
    /// Scans every resolved dependency for a module/type collision in each slice
    /// independently. The simulator wrapper-compile only patches collisions present in
    /// its slice; ditto device. This avoids the over-patch case where a device-only
    /// <c>@interface ModuleName</c> would otherwise cause the simulator pre-compile
    /// to strip a qualifier that is unambiguous on simulator.
    /// </summary>
    public static SlicedCollisionResult DetectPerSlice(
        IReadOnlyList<FrameworkDependencyInfo>? resolvedDependencies,
        PlatformInfo platformInfo,
        ILogger logger)
    {
        var simulator = new List<string>();
        var device = new List<string>();
        if (resolvedDependencies == null || resolvedDependencies.Count == 0)
            return new SlicedCollisionResult(simulator, device);

        foreach (var dep in resolvedDependencies)
        {
            if (string.IsNullOrEmpty(dep.ModuleName) || string.IsNullOrEmpty(dep.XCFrameworkPath))
                continue;
            if (!string.IsNullOrEmpty(dep.SimulatorFrameworkSearchPath) &&
                TryDetectCollisionInSlice(dep, dep.SimulatorFrameworkSearchPath!, logger))
            {
                LogDetected(logger, dep.ModuleName, "simulator");
                simulator.Add(dep.ModuleName);
            }
            if (!string.IsNullOrEmpty(dep.DeviceFrameworkSearchPath) &&
                TryDetectCollisionInSlice(dep, dep.DeviceFrameworkSearchPath!, logger))
            {
                LogDetected(logger, dep.ModuleName, "device");
                device.Add(dep.ModuleName);
            }
        }
        return new SlicedCollisionResult(simulator, device);
    }

    /// <summary>
    /// Legacy entry point: returns the union of <see cref="DetectPerSlice"/> across both
    /// slices. Kept for callers that don't yet differentiate by slice. New code should
    /// prefer <see cref="DetectPerSlice"/> to avoid over-patching the slice that didn't
    /// actually expose the collision.
    /// </summary>
    public static List<string> Detect(
        IReadOnlyList<FrameworkDependencyInfo>? resolvedDependencies,
        PlatformInfo platformInfo,
        ILogger logger)
    {
        var sliced = DetectPerSlice(resolvedDependencies, platformInfo, logger);
        var union = new HashSet<string>(sliced.Simulator);
        union.UnionWith(sliced.Device);
        return union.ToList();
    }

    private static void LogDetected(ILogger logger, string moduleName, string sliceLabel)
    {
        logger.LogInformation(
            "Detected dep-module/type name collision ({Slice}): dependency '{Module1}' exports a public type with the same name. Will pre-compile bound module's swiftinterface with `{Module2}.` qualifier stripped for this slice.",
            sliceLabel, moduleName, moduleName);
    }

    /// <summary>
    /// Tests whether a single dep exports a public type/class whose name matches the
    /// dep module name, scoped to a specific slice's framework search path. Returns
    /// false when neither the Swift nor ObjC inspection paths fire (no swiftinterface,
    /// no headers directory).
    /// </summary>
    internal static bool TryDetectCollisionInSlice(
        FrameworkDependencyInfo dep,
        string sliceSearchPath,
        ILogger logger)
    {
        // 1. Swift swiftinterface scan (preferred, faster, more authoritative).
        var modulesDir = Path.Combine(sliceSearchPath, dep.ModuleName + ".framework", "Modules", dep.ModuleName + ".swiftmodule");
        if (Directory.Exists(modulesDir))
        {
            string? chosen = null;
            foreach (var ext in new[] { ".private.swiftinterface", ".swiftinterface" })
            {
                foreach (var path in Directory.EnumerateFiles(modulesDir, "*" + ext))
                {
                    chosen = path;
                    break;
                }
                if (chosen != null)
                    break;
            }
            if (chosen != null)
            {
                try
                {
                    var text = File.ReadAllText(chosen);
                    if (HasSwiftPublicTypeWithName(text, dep.ModuleName))
                        return true;
                }
                catch (IOException ex)
                {
                    logger.LogWarning("Could not read swiftinterface for dep '{Module}' at '{Path}': {Message}",
                        dep.ModuleName, chosen, ex.Message);
                }
            }
        }

        // 2. Objective-C header scan (umbrella first, fall through to all headers).
        // ObjC-only deps (IsObjCOnly == true) only have this path. Swift deps may
        // also export ObjC classes; we still scan headers for completeness if the
        // swiftinterface path didn't fire.
        var headersDir = Path.Combine(sliceSearchPath, dep.ModuleName + ".framework", "Headers");
        if (Directory.Exists(headersDir))
        {
            try
            {
                if (HasObjCInterfaceInHeaders(headersDir, dep.ModuleName))
                    return true;
            }
            catch (IOException ex)
            {
                logger.LogWarning("Could not read headers for dep '{Module}' at '{Path}': {Message}",
                    dep.ModuleName, headersDir, ex.Message);
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="swiftInterfaceText"/> declares a public type — nominal
    /// or <c>typealias</c> — named <paramref name="moduleName"/>, i.e. the module shadows its own
    /// name in type scope. Used both for dependency modules and for the bound module itself.
    /// </summary>
    public static bool HasSwiftPublicTypeWithName(string swiftInterfaceText, string moduleName)
    {
        if (string.IsNullOrEmpty(swiftInterfaceText) || string.IsNullOrEmpty(moduleName))
            return false;
        foreach (Match match in SwiftPublicTypeRegex.Matches(swiftInterfaceText))
        {
            if (match.Groups[1].Value == moduleName)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when any <c>.h</c> file under <paramref name="headersDir"/> contains
    /// an <c>@interface &lt;moduleName&gt;</c> declaration. Scans the umbrella header
    /// (<c>&lt;moduleName&gt;.h</c>) first, falling through to every other <c>.h</c>
    /// file when the umbrella has no match — many Apple-flavored umbrellas only
    /// <c>#import</c> the real declarations from sibling headers.
    /// </summary>
    public static bool HasObjCInterfaceInHeaders(string headersDir, string moduleName)
    {
        if (string.IsNullOrEmpty(headersDir) || string.IsNullOrEmpty(moduleName))
            return false;
        if (!Directory.Exists(headersDir))
            return false;

        // Try the umbrella header first.
        var umbrella = Path.Combine(headersDir, moduleName + ".h");
        if (File.Exists(umbrella) && HasObjCInterfaceInFile(umbrella, moduleName))
            return true;

        // Fall through to all other headers.
        foreach (var headerPath in Directory.EnumerateFiles(headersDir, "*.h", SearchOption.AllDirectories))
        {
            if (string.Equals(headerPath, umbrella, StringComparison.Ordinal))
                continue;
            if (HasObjCInterfaceInFile(headerPath, moduleName))
                return true;
        }
        return false;
    }

    private static bool HasObjCInterfaceInFile(string path, string moduleName)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return false;
        }
        foreach (Match match in ObjCInterfaceRegex.Matches(text))
        {
            if (match.Groups[1].Value == moduleName)
                return true;
        }
        return false;
    }

}
