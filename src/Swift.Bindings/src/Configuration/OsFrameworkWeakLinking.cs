// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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

        // Providers a supplied OS symbol inventory lists (--os-symbol-inventory). The inventory describes
        // them as an OS release ships them, so they are weak-linked and audited like an OS framework.
        private static readonly ConcurrentDictionary<string, byte> s_suppliedProviders = new(StringComparer.Ordinal);

        /// <summary>
        /// Whether <paramref name="installName"/> names a dylib the OS provides under <c>/System/</c>, or a
        /// provider a supplied OS symbol inventory lists (<see cref="TreatAsOsProviders"/>).
        /// </summary>
        internal static bool IsOsResident(string installName) =>
            installName.StartsWith("/System/", StringComparison.Ordinal) || s_suppliedProviders.ContainsKey(installName);

        /// <summary>Treats the providers a supplied OS symbol inventory lists as OS-resident for this process.</summary>
        internal static void TreatAsOsProviders(IEnumerable<string> installNames)
        {
            foreach (var name in installNames)
                s_suppliedProviders[name] = 0;
        }

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
        /// The SDK (<c>xcrun --sdk</c> name) of the platform the <c>LC_BUILD_VERSION</c> in
        /// <paramref name="otoolOutput"/> names, or null when there is none. Mac Catalyst links against
        /// the macOS SDK.
        /// </summary>
        internal static string? BuildPlatformSdk(string otoolOutput)
        {
            bool inBuildVersion = false;
            foreach (var raw in otoolOutput.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("cmd ", StringComparison.Ordinal))
                {
                    inBuildVersion = line.Substring(4).Trim() == "LC_BUILD_VERSION";
                    continue;
                }
                if (inBuildVersion && line.StartsWith("platform ", StringComparison.Ordinal))
                {
                    return line.Substring(9).Trim() switch
                    {
                        "1" or "MACOS" => "macosx",
                        "2" or "IOS" => "iphoneos",
                        "3" or "TVOS" => "appletvos",
                        "6" or "MACCATALYST" => "macosx",
                        "7" or "IOSSIMULATOR" => "iphonesimulator",
                        "8" or "TVOSSIMULATOR" => "appletvsimulator",
                        _ => null,
                    };
                }
            }
            return null;
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

        /// <summary>One symbol a linked image imports, as <c>dyld_info -imports</c> prints it.</summary>
        internal sealed record ImportedSymbol(string Arch, string Symbol, string From, bool Weak);

        private static readonly Regex ImportHeader = new(@"^(?<path>.+) \[(?<arch>[^\]]+)\]:\s*$", RegexOptions.Compiled);
        private static readonly Regex ImportLine = new(
            @"^\s*0x[0-9A-Fa-f]+\s+(?<symbol>\S+)\s+(?<weak>\[weak-import\]\s+)?\(from (?<from>[^)]+)\)\s*$",
            RegexOptions.Compiled);

        /// <summary>Parses <c>dyld_info -imports</c> output: one entry per imported symbol per architecture.</summary>
        internal static IReadOnlyList<ImportedSymbol> ParseImports(string text)
        {
            var result = new List<ImportedSymbol>();
            string arch = "";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var header = ImportHeader.Match(line);
                if (header.Success)
                {
                    arch = header.Groups["arch"].Value;
                    continue;
                }
                var import = ImportLine.Match(line);
                if (import.Success)
                    result.Add(new ImportedSymbol(arch, import.Groups["symbol"].Value, import.Groups["from"].Value,
                        import.Groups["weak"].Success));
            }
            return result;
        }

        /// <summary>
        /// The name <c>dyld_info</c> prints after "from" for a dylib: its install name's last path
        /// component up to the first dot (<c>Foundation</c>, <c>libswiftCore</c>, <c>libSystem</c>).
        /// </summary>
        internal static string ImportLeaf(string installName)
        {
            var file = installName.Substring(installName.LastIndexOf('/') + 1);
            var dot = file.IndexOf('.');
            return dot < 0 ? file : file.Substring(0, dot);
        }

        /// <summary>
        /// Whether the SDK's stub for <paramref name="installName"/> re-exports a library only its allowed
        /// clients may link (SwiftUI re-exports SwiftUICore). A symbol of that library binds through the
        /// umbrella, and the linker then keeps the umbrella's load strong whatever <c>-weak_framework</c>
        /// asks; <c>-weak_framework</c> still makes every import from it weak.
        /// </summary>
        internal static bool ReexportsRestrictedLibrary(string sdkPath, string installName)
        {
            var stub = Path.Combine(sdkPath, installName.TrimStart('/') + ".tbd");
            if (!File.Exists(stub))
                return false;
            var text = File.ReadAllText(stub);
            var section = Regex.Match(text, @"^reexported-libraries:\s*\n(?<body>(?:[ \t-].*\n)+)", RegexOptions.Multiline);
            if (!section.Success)
                return false;
            foreach (Match name in Regex.Matches(section.Groups["body"].Value, @"'(?<n>/[^']+)'|(?<n>/[^\s,\]]+)"))
            {
                var reexported = Path.Combine(sdkPath, name.Groups["n"].Value.TrimStart('/') + ".tbd");
                if (File.Exists(reexported)
                    && Regex.IsMatch(File.ReadAllText(reexported), @"^allowable-clients:", RegexOptions.Multiline))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The strong OS-resident loads among <paramref name="strongInstallNames"/> that would fail on an
        /// older OS: those with a strong import, those the linker had no reason to keep strong (it keeps a
        /// load strong only for an umbrella that re-exports a restricted library, see
        /// <see cref="ReexportsRestrictedLibrary"/>), and an umbrella the OS symbol inventory's OS may not
        /// have at all (<see cref="PresentOnInventoryOs"/>), which dyld refuses however weak its imports
        /// are. Without an SDK path every strong load is unsafe.
        /// </summary>
        internal static IReadOnlyList<string> UnsafeStrongLoads(
            IEnumerable<string> strongInstallNames, IReadOnlyList<ImportedSymbol> imports, string? sdkPath,
            OsSymbolInventory inventory)
        {
            var strongLeaves = new HashSet<string>(imports.Where(i => !i.Weak).Select(i => i.From), StringComparer.Ordinal);
            return strongInstallNames
                .Where(n => strongLeaves.Contains(ImportLeaf(n))
                            || string.IsNullOrEmpty(sdkPath)
                            || !ReexportsRestrictedLibrary(sdkPath, n)
                            || !PresentOnInventoryOs(n, imports, sdkPath, inventory))
                .ToList();
        }

        /// <summary>
        /// Whether the OS symbol inventory's OS has the framework at <paramref name="installName"/>: one of
        /// the Swift symbols the binary imports from it ships there, on the variant of the SDK the binary
        /// was linked against. A framework the generating SDK added has no such import. An import list with
        /// no Swift symbol from it, or an SDK whose variant is unknown, proves nothing and answers false.
        /// </summary>
        internal static bool PresentOnInventoryOs(
            string installName, IReadOnlyList<ImportedSymbol> imports, string sdkPath, OsSymbolInventory inventory)
        {
            var variant = InventoryVariant(sdkPath, installName);
            if (variant is null)
                return false;
            var leaf = ImportLeaf(installName);
            return imports.Any(i => i.From == leaf && i.Symbol.StartsWith("_$s", StringComparison.Ordinal)
                                    && inventory.ShipsOn(variant, i.Symbol.Substring(1)));
        }

        /// <summary>
        /// The inventory variant of a binary linked against <paramref name="sdkPath"/> (named for its SDK,
        /// <c>iPhoneSimulator27.2.sdk</c> is <c>ios-simulator</c>); a macOS SDK load of an iOSSupport framework is
        /// Mac Catalyst's. Null for an SDK the inventory has no variant for.
        /// </summary>
        internal static string? InventoryVariant(string sdkPath, string installName)
        {
            var sdk = Path.GetFileName(sdkPath.TrimEnd('/'));
            if (sdk.StartsWith("iPhoneSimulator", StringComparison.Ordinal)) return "ios-simulator";
            if (sdk.StartsWith("iPhoneOS", StringComparison.Ordinal)) return "ios";
            if (sdk.StartsWith("AppleTVSimulator", StringComparison.Ordinal)) return "tvos-simulator";
            if (sdk.StartsWith("AppleTVOS", StringComparison.Ordinal)) return "tvos";
            if (sdk.StartsWith("MacOSX", StringComparison.Ordinal))
                return installName.StartsWith("/System/iOSSupport/", StringComparison.Ordinal) ? "maccatalyst" : "macos";
            return null;
        }

        /// <summary>The failure message for a wrapper that still loads an OS dylib strongly after its relink.</summary>
        internal static string StillStrongMessage(string wrapperModuleName, string binary, IEnumerable<string> strong) =>
            $"The {wrapperModuleName} wrapper at '{binary}' still loads OS-resident dylib(s) strongly after weak-linking: " +
            $"{string.Join(", ", strong)}. A strong load of an OS framework makes dyld refuse the whole wrapper on an OS " +
            "that lacks one of its symbols, so the wrapper is not shipped.";
    }
}
