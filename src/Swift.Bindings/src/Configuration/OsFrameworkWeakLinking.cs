// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration
{
    /// <summary>
    /// Weak-links every OS-resident framework a wrapper loads, so a symbol the running OS lacks can never
    /// stop the wrapper loading (load-safety property P1).
    /// <para/>
    /// Swift decides a reference's weak linkage from the imported declaration's availability, not from any
    /// <c>@available</c> on the caller. A member an SDK declares without a floor, but that an older OS does
    /// not ship (Xcode 27.2's <c>ChaChaPoly.Nonce.count</c>, absent from iOS 26), is therefore a strong
    /// import, and dyld refuses to load the whole wrapper, and the app with it, on that older OS. Only the
    /// linker can make the reference weak regardless of what the interface declares: <c>-weak_framework X</c>
    /// loads X with <c>LC_LOAD_WEAK_DYLIB</c> and marks every import from it weak. It wins over an explicit
    /// <c>-framework X</c> and over Swift's autolink of X in either order (measured with ld 1230 and 27106).
    /// <para/>
    /// Which frameworks a link loads is only known after it ran: Swift autolinks the whole import closure
    /// and the linker keeps only the dylibs whose symbols are used. The compilers therefore link, read the
    /// linked load commands back, and relink once with <c>-weak_framework</c> for every OS-resident
    /// framework that came out strong; a second strong result fails the compile. The set a module needed is
    /// remembered for its next slice, so normally only its first slice relinks.
    /// <para/>
    /// OS-resident means an install name under <c>/System/</c> (frameworks, sub-frameworks and the Mac
    /// Catalyst <c>/System/iOSSupport</c> tree). <c>/usr/lib</c>, including <c>/usr/lib/swift</c>, is not
    /// weakened: the Swift runtime and libSystem are present on every supported OS and their symbols carry
    /// accurate availability. Embedded (app-bundled) frameworks stay normal: the app ships them.
    /// </summary>
    internal static partial class OsFrameworkWeakLinking
    {
        internal enum LoadKind { Load, Weak, Reexport, Upward, Lazy }

        internal readonly record struct LoadedDylib(string InstallName, LoadKind Kind);

        /// <summary>Whether <paramref name="installName"/> names a dylib the OS provides under <c>/System/</c>.</summary>
        internal static bool IsOsResident(string installName) =>
            installName.StartsWith("/System/", StringComparison.Ordinal);

        /// <summary>
        /// The framework name of an install name (<c>/System/Library/Frameworks/X.framework/X</c> or
        /// <c>…/X.framework/Versions/A/X</c> → <c>X</c>), or null when it is not a framework.
        /// </summary>
        internal static string? FrameworkName(string installName)
        {
            var segments = installName.Split('/');
            for (int i = segments.Length - 2; i >= 0; i--)
            {
                if (segments[i].EndsWith(".framework", StringComparison.Ordinal) && segments[i].Length > ".framework".Length)
                    return segments[i].Substring(0, segments[i].Length - ".framework".Length);
            }
            return null;
        }

        /// <summary>
        /// Parses <c>otool -l</c> output into the dylib load commands it lists. A fat binary lists each
        /// architecture's commands in turn; the result is their concatenation.
        /// </summary>
        internal static IReadOnlyList<LoadedDylib> ParseLoadCommands(string otoolOutput)
        {
            var result = new List<LoadedDylib>();
            LoadKind? pending = null;
            foreach (var raw in otoolOutput.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("cmd ", StringComparison.Ordinal))
                {
                    pending = line.Substring(4).Trim() switch
                    {
                        "LC_LOAD_DYLIB" => LoadKind.Load,
                        "LC_LOAD_WEAK_DYLIB" => LoadKind.Weak,
                        "LC_REEXPORT_DYLIB" => LoadKind.Reexport,
                        "LC_LOAD_UPWARD_DYLIB" => LoadKind.Upward,
                        "LC_LAZY_LOAD_DYLIB" => LoadKind.Lazy,
                        _ => null,
                    };
                    continue;
                }
                if (pending is { } kind && line.StartsWith("name ", StringComparison.Ordinal))
                {
                    var name = line.Substring(5);
                    var offset = name.LastIndexOf(" (offset ", StringComparison.Ordinal);
                    if (offset >= 0)
                        name = name.Substring(0, offset);
                    result.Add(new LoadedDylib(name.Trim(), kind));
                    pending = null;
                }
            }
            return result;
        }

        /// <summary>
        /// The OS-resident dylibs <paramref name="loads"/> load in a way a missing symbol would fail: every
        /// <c>/System/</c> load that is not <c>LC_LOAD_WEAK_DYLIB</c>, by install name, ordinal order.
        /// </summary>
        internal static IReadOnlyList<string> StrongOsResidentLoads(IEnumerable<LoadedDylib> loads) =>
            loads.Where(l => l.Kind != LoadKind.Weak && IsOsResident(l.InstallName))
                .Select(l => l.InstallName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        /// <summary>Every OS-resident framework <paramref name="loads"/> load, weak or not, by framework name.</summary>
        internal static IReadOnlyList<string> OsResidentFrameworks(IEnumerable<LoadedDylib> loads) =>
            loads.Where(l => IsOsResident(l.InstallName))
                .Select(l => FrameworkName(l.InstallName))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        /// <summary>The swiftc arguments that weak-link <paramref name="frameworks"/>, ordinal order.</summary>
        internal static string SwiftcFlags(IEnumerable<string> frameworks) =>
            string.Concat(frameworks.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => $"-Xlinker -weak_framework -Xlinker {f} "));

        /// <summary>The clang arguments that weak-link <paramref name="frameworks"/>, ordinal order.</summary>
        internal static string ClangFlags(IEnumerable<string> frameworks) =>
            string.Concat(frameworks.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => $"-Wl,-weak_framework,{f} "));

        // The OS frameworks each wrapper module weak-linked on a slice it already linked, so its other
        // slices link right the first time. A miss only costs a relink.
        private static readonly ConcurrentDictionary<string, SortedSet<string>> s_learned = new(StringComparer.Ordinal);

        /// <summary>
        /// The frameworks to weak-link on the first link of a slice of <paramref name="wrapperModuleName"/>:
        /// those an earlier slice of the module needed, plus the bound module when it is the SDK's own
        /// framework (an Apple-framework binding, whose search path is inside the SDK).
        /// </summary>
        internal static SortedSet<string> Predict(string wrapperModuleName, string? boundModuleName, string? frameworkSearchPath, string? sdkPath)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (s_learned.TryGetValue(wrapperModuleName, out var learned))
            {
                lock (learned)
                    set.UnionWith(learned);
            }
            if (!string.IsNullOrEmpty(boundModuleName) && IsInsideSdk(frameworkSearchPath, sdkPath))
                set.Add(boundModuleName);
            return set;
        }

        /// <summary>Remembers the OS frameworks a slice of <paramref name="wrapperModuleName"/> weak-linked.</summary>
        internal static void Learn(string wrapperModuleName, IEnumerable<string> frameworks)
        {
            var learned = s_learned.GetOrAdd(wrapperModuleName, _ => new SortedSet<string>(StringComparer.Ordinal));
            lock (learned)
                learned.UnionWith(frameworks);
        }

        internal static bool IsInsideSdk(string? path, string? sdkPath)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sdkPath))
                return false;
            var sdk = sdkPath.TrimEnd('/') + "/";
            return (path.TrimEnd('/') + "/").StartsWith(sdk, StringComparison.Ordinal);
        }

        /// <summary>The failure message for a wrapper that still loads an OS dylib strongly after its relink.</summary>
        internal static string StillStrongMessage(string wrapperModuleName, string binary, IEnumerable<string> strong) =>
            $"The {wrapperModuleName} wrapper at '{binary}' still loads OS-resident dylib(s) strongly after weak-linking: " +
            $"{string.Join(", ", strong)}. A strong load of an OS framework makes dyld refuse the whole wrapper on an OS " +
            "that lacks one of its symbols, so the wrapper is not shipped.";
    }
}
