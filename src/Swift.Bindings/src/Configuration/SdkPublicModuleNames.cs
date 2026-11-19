// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// The public module an SDK module must be imported through, read from the module's own
    /// interface. Swift 6.4 SDK modules that are implementation details of an umbrella declare it
    /// with <c>-public-module-name &lt;Umbrella&gt;</c> in their interface's module flags
    /// (<c>swift-module-flags-ignorable</c>), and swiftc refuses a direct <c>import</c> of such a module
    /// when it is loaded from the SDK ("module 'X' is an implementation detail of 'Y'; import 'Y'
    /// instead"). The Xcode 27.2 SDKs carry several of them (RealityKit's sub-frameworks under
    /// <c>System/Library/SubFrameworks/</c>, AppIntentsTypeSupport), and every Swift file the generator
    /// compiles imports modules by the names the bound interface uses, so each such import has to be
    /// rewritten to the public module.
    ///
    /// The interface is the authority: it is what swiftc itself reads, so the rewrite stays correct as
    /// SDKs add or drop sub-modules. The hand-kept <c>compileImportModule</c> entries of
    /// <c>apple-frameworks.json</c> remain the fallback when no SDK interface is reachable (and stay
    /// for their reverse lookup, which names a source module's own types through its umbrella).
    ///
    /// One instance serves one generator run, installed with <see cref="Install"/>; the installed value
    /// is <see cref="AsyncLocal{T}"/>-scoped so concurrently running tests never observe one another's.
    /// Lookups that cannot resolve an SDK, or find no interface for a module, answer null: the import is
    /// then left as written, which is exactly the pre-existing behaviour.
    /// </summary>
    internal sealed class SdkPublicModuleNames
    {
        private static readonly AsyncLocal<SdkPublicModuleNames?> _current = new();

        private readonly Lazy<IReadOnlyList<string>> _sdkRoots;
        private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _asymmetricCache = new(StringComparer.Ordinal);

        /// <summary>The lookup installed for the current run, or null outside one.</summary>
        internal static SdkPublicModuleNames? Current => _current.Value;

        /// <summary>The SDK of every slice of the run, resolved on first use.</summary>
        internal IReadOnlyList<string> SdkRoots => _sdkRoots.Value;

        internal SdkPublicModuleNames(params string?[] sdkRoots) =>
            _sdkRoots = new Lazy<IReadOnlyList<string>>(() => NonEmpty(sdkRoots ?? Array.Empty<string?>()));

        private SdkPublicModuleNames(Func<IReadOnlyList<string>> sdkRoots) =>
            _sdkRoots = new Lazy<IReadOnlyList<string>>(sdkRoots, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// A lookup against the SDK of every slice of <paramref name="platformInfo"/>, resolved through
        /// xcrun on first use (so a run that never asks pays nothing). One wrapper source is compiled for
        /// every slice, and the slices' SDKs do not hold the same modules (a device SDK can carry an
        /// implementation-detail module its simulator SDK lacks), so a module is looked up in each.
        /// </summary>
        internal static SdkPublicModuleNames ForPlatform(PlatformInfo platformInfo, ICommandRunner commandRunner, ILogger logger) =>
            new(() => NonEmpty(platformInfo.AllSlices.Select(slice =>
            {
                try
                {
                    return SwiftWrapperCompiler.ResolveSdkPath(slice.SdkName, commandRunner).Trim();
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Could not resolve the {Sdk} SDK to read public module names from: {Message}", slice.SdkName, ex.Message);
                    return null;
                }
            })));

        private static IReadOnlyList<string> NonEmpty(IEnumerable<string?> roots) =>
            roots.Where(r => !string.IsNullOrEmpty(r)).Select(r => r!).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>Makes <paramref name="names"/> the current lookup until the returned scope is disposed.</summary>
        internal static IDisposable Install(SdkPublicModuleNames? names)
        {
            var previous = _current.Value;
            _current.Value = names;
            return new Scope(previous);
        }

        /// <summary>
        /// The public module <paramref name="module"/> declares, or null when it declares none, names
        /// itself, or has no interface in the SDK.
        /// </summary>
        internal string? TryGetPublicModuleName(string module)
        {
            if (string.IsNullOrEmpty(module) || _sdkRoots.Value.Count == 0)
                return null;
            return _cache.GetOrAdd(module, m =>
            {
                // Any slice's SDK that declares a public name for the module answers for it.
                foreach (var sdk in _sdkRoots.Value)
                {
                    var interfacePath = FindModuleInterface(sdk, m);
                    if (interfacePath is null)
                        continue;
                    try
                    {
                        var name = ReadPublicModuleName(interfacePath);
                        if (name is not null && !string.Equals(name, m, StringComparison.Ordinal))
                            return name;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // An unreadable interface declares nothing; another slice's may.
                    }
                }
                return null;
            });
        }

        /// <summary>
        /// True when <paramref name="module"/> ships in some of this run's slice SDKs and not in
        /// others. One wrapper source is compiled for every slice, and the SDKs do not hold the same
        /// modules: a device SDK can carry a framework its simulator SDK lacks (Xcode 27.2's CoreAI is
        /// in the iPhoneOS, AppleTVOS and MacOSX SDKs, absent from both simulators, and
        /// MetalPerformanceShadersGraph's device interface imports it), so a plain <c>import</c> of such
        /// a module fails every slice whose SDK lacks it. A module present in no slice SDK (a bound
        /// library's own dependency) or in all of them answers false, as does a run with fewer than two
        /// distinct SDKs.
        /// </summary>
        internal bool IsSliceAsymmetric(string module)
        {
            if (string.IsNullOrEmpty(module) || _sdkRoots.Value.Count < 2)
                return false;
            return _asymmetricCache.GetOrAdd(module, m =>
            {
                var present = 0;
                foreach (var sdk in _sdkRoots.Value)
                {
                    if (ShipsModule(sdk, m))
                        present++;
                }
                return present > 0 && present < _sdkRoots.Value.Count;
            });
        }

        /// <summary>
        /// Whether <paramref name="sdkRoot"/> ships <paramref name="module"/>: as a framework under
        /// one of its <see cref="FrameworkDirectories"/>, or as a Swift module of its toolchain overlay
        /// directory (<c>usr/lib/swift/&lt;M&gt;.swiftmodule</c>).
        /// </summary>
        internal static bool ShipsModule(string sdkRoot, string module)
        {
            foreach (var frameworks in FrameworkDirectories(sdkRoot))
            {
                if (Directory.Exists(Path.Combine(frameworks, $"{module}.framework")))
                    return true;
            }
            return Directory.Exists(Path.Combine(sdkRoot, "usr", "lib", "swift", $"{module}.swiftmodule"))
                || File.Exists(Path.Combine(sdkRoot, "usr", "lib", "swift", $"{module}.swiftinterface"));
        }

        /// <summary>
        /// The Swift lines importing <paramref name="module"/> into a wrapper source: a plain
        /// <c>import</c>, or, when the run's slice SDKs disagree on whether the module exists
        /// (<see cref="IsSliceAsymmetric"/>), the same import under <c>#if canImport(...)</c> so each
        /// slice compiles with the modules its own SDK has. A declaration that names one of the
        /// module's types still needs that module on the slice it compiles for; verify-recover
        /// withdraws it there like any other member that does not compile.
        /// </summary>
        internal static IReadOnlyList<string> ImportLines(string module)
        {
            var line = $"import {module}";
            // canImport takes the top-level module; a submodule import is guarded on its parent.
            var topLevel = module.Split('.')[0];
            return Current?.IsSliceAsymmetric(topLevel) == true
                ? new[] { $"#if canImport({topLevel})", line, "#endif" }
                : new[] { line };
        }

        /// <summary>The framework directories of an SDK a module's interface can live under.</summary>
        internal static IEnumerable<string> FrameworkDirectories(string sdkRoot)
        {
            foreach (var prefix in new[] { "", Path.Combine("System", "iOSSupport") })
            {
                yield return Path.Combine(sdkRoot, prefix, "System", "Library", "Frameworks");
                yield return Path.Combine(sdkRoot, prefix, "System", "Library", "SubFrameworks");
            }
        }

        /// <summary>
        /// The public textual interface of <paramref name="module"/> in <paramref name="sdkRoot"/>
        /// (<c>&lt;M&gt;.framework/Modules/&lt;M&gt;.swiftmodule/*.swiftinterface</c>, private and package
        /// interfaces excluded), first in ordinal order; null when the SDK holds none.
        /// </summary>
        internal static string? FindModuleInterface(string sdkRoot, string module)
        {
            foreach (var frameworks in FrameworkDirectories(sdkRoot))
            {
                var moduleDir = Path.Combine(frameworks, $"{module}.framework", "Modules", $"{module}.swiftmodule");
                if (!Directory.Exists(moduleDir))
                    continue;
                var candidate = Directory.EnumerateFiles(moduleDir, "*.swiftinterface")
                    .Where(p => !p.EndsWith(".private.swiftinterface", StringComparison.Ordinal)
                        && !p.EndsWith(".package.swiftinterface", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault();
                if (candidate is not null)
                    return candidate;
            }
            return null;
        }

        /// <summary>Reads the header of the interface at <paramref name="interfacePath"/> for its public module name.</summary>
        internal static string? ReadPublicModuleName(string interfacePath)
        {
            using var reader = new StreamReader(interfacePath);
            var header = new List<string>();
            // The flags sit in the leading comment block; stop at the first declaration.
            for (var i = 0; i < 64 && reader.ReadLine() is { } line; i++)
            {
                if (!line.StartsWith("//", StringComparison.Ordinal))
                    break;
                header.Add(line);
            }
            return ParsePublicModuleName(header);
        }

        /// <summary>
        /// The value of <c>-public-module-name</c> in the <c>swift-module-flags</c> or
        /// <c>swift-module-flags-ignorable</c> lines of an interface header, or null when absent.
        /// </summary>
        internal static string? ParsePublicModuleName(IEnumerable<string> headerLines)
        {
            foreach (var line in headerLines)
            {
                var body = line.TrimStart('/', ' ');
                var colon = body.IndexOf(':');
                if (colon < 0)
                    continue;
                var key = body[..colon].Trim();
                if (key is not ("swift-module-flags" or "swift-module-flags-ignorable"))
                    continue;
                var tokens = body[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (var t = 0; t < tokens.Length; t++)
                {
                    if (tokens[t] == "-public-module-name" && t + 1 < tokens.Length)
                        return tokens[t + 1];
                    if (tokens[t].StartsWith("-public-module-name=", StringComparison.Ordinal))
                        return tokens[t]["-public-module-name=".Length..];
                }
            }
            return null;
        }

        private sealed class Scope(SdkPublicModuleNames? previous) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _current.Value = previous;
            }
        }
    }
}
