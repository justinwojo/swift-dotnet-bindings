// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BindingsGeneration
{
    /// <summary>
    /// The link inputs a wrapper needs from its <c>--framework-dependency</c> slices beyond the
    /// <c>-framework</c> names <see cref="SwiftWrapperCompiler.CollectTransitiveFrameworkNames"/> finds.
    /// <para/>
    /// A library-type xcframework slice ships a bare <c>libFoo.a</c> or <c>libFoo.dylib</c> (its
    /// Info.plist <c>LibraryPath</c>) next to a headers directory, not a <c>Foo.framework</c> bundle, so a
    /// <c>-F</c> over the slice finds nothing to link. When the wrapper force-loads a static primary whose
    /// objects call into such a library, that library has to be named on the link line by path, or every
    /// symbol the primary takes from it is undefined.
    /// <para/>
    /// A static archive also carries none of the load commands of the system frameworks and libraries its
    /// objects use. A clang module declares them with <c>link</c> / <c>link framework</c>, and clang turns
    /// those into autolink options in every object that imports the module. The wrapper never imports a
    /// dependency's module, so for each static dependency archive it links, the dependency's own
    /// <c>module.modulemap</c> <c>link</c> declarations are applied here instead. As with autolinking, a
    /// declaration that names nothing in the SDK is left out rather than failing the link.
    /// </summary>
    internal static class DependencyLinkInputs
    {
        /// <summary>System frameworks and libraries a dependency's module map declares.</summary>
        internal sealed record Autolinks(IReadOnlyList<string> Frameworks, IReadOnlyList<string> Libraries)
        {
            internal static readonly Autolinks None = new(Array.Empty<string>(), Array.Empty<string>());
        }

        /// <summary>
        /// The bare library binaries (<c>.a</c> archive or <c>.dylib</c>) of the library-type xcframework
        /// slices among <paramref name="searchPaths"/>, in search-path order and de-duplicated. A slice is
        /// read from its xcframework's Info.plist: the directory must be an xcframework slice whose
        /// <c>LibraryPath</c> is not a <c>.framework</c>, and the file there must be a real Mach-O or
        /// <c>ar</c> binary. Framework-type slices and plain directories contribute nothing here.
        /// </summary>
        internal static IReadOnlyList<string> CollectLibraryBinaries(IReadOnlyList<string>? searchPaths)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slice in EnumerateSlices(searchPaths))
            {
                if (slice.Binary is { } binary && seen.Add(binary))
                    result.Add(binary);
            }
            return result;
        }

        /// <summary>
        /// Whether the xcframework at <paramref name="xcframeworkPath"/> declares a library-type slice: an
        /// Info.plist entry whose <c>LibraryPath</c> is a bare library rather than a <c>.framework</c>.
        /// False when the Info.plist is missing or unreadable.
        /// </summary>
        internal static bool DeclaresLibrarySlices(string xcframeworkPath)
        {
            var plist = Path.Combine(xcframeworkPath, "Info.plist");
            if (!File.Exists(plist))
                return false;
            try
            {
                return XCFrameworkResolver.ParseInfoPlist(plist, null, NullLogger.Instance).Any(s =>
                    !string.IsNullOrEmpty(s.LibraryPath)
                    && !s.LibraryPath.EndsWith(".framework", StringComparison.Ordinal));
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The <c>link</c> declarations of every static dependency archive the wrapper links from
        /// <paramref name="searchPaths"/>: a library-type slice's archive (module maps under its headers
        /// directory) and a framework-type slice's static framework binary (its <c>Modules/module.modulemap</c>).
        /// Only names the SDK at <paramref name="sdkPath"/> can link for <paramref name="targetTriple"/> are
        /// kept; a dynamic dependency is skipped because its own load commands already name what it needs.
        /// </summary>
        internal static Autolinks CollectStaticArchiveAutolinks(
            IReadOnlyList<string>? searchPaths, string? sdkPath, string targetTriple)
        {
            // Mac Catalyst links the iOS frameworks the SDK keeps under iOSSupport; every link path adds
            // that directory as a -F for a -macabi target, and no other target can use it.
            var iOSSupportFrameworks = string.IsNullOrEmpty(sdkPath)
                ? null
                : SwiftWrapperCompiler.TryGetMacCatalystIOSSupportFrameworkPath(targetTriple, sdkPath);
            var frameworks = new List<string>();
            var libraries = new List<string>();
            var seenFrameworks = new HashSet<string>(StringComparer.Ordinal);
            var seenLibraries = new HashSet<string>(StringComparer.Ordinal);

            void AddFrom(string modulemap)
            {
                string text;
                try { text = File.ReadAllText(modulemap); }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
                var links = ParseModuleMapLinks(text);
                foreach (var fw in links.Frameworks)
                {
                    if (SdkProvidesFramework(sdkPath, fw, iOSSupportFrameworks) && seenFrameworks.Add(fw))
                        frameworks.Add(fw);
                }
                foreach (var lib in links.Libraries)
                {
                    if (SdkProvidesLibrary(sdkPath, lib) && seenLibraries.Add(lib))
                        libraries.Add(lib);
                }
            }

            foreach (var slice in EnumerateSlices(searchPaths))
            {
                if (slice.Binary is { } binary)
                {
                    if (!IsStaticArchive(binary))
                        continue;
                    var headers = Path.Combine(slice.SliceDir, string.IsNullOrEmpty(slice.HeadersPath) ? "Headers" : slice.HeadersPath);
                    foreach (var modulemap in EnumerateModuleMaps(headers))
                        AddFrom(modulemap);
                }
            }

            if (searchPaths != null)
            {
                foreach (var path in searchPaths)
                {
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                        continue;
                    foreach (var frameworkDir in Directory.EnumerateDirectories(path, "*.framework"))
                    {
                        var name = Path.GetFileNameWithoutExtension(frameworkDir);
                        if (string.IsNullOrEmpty(name) || !IsStaticArchive(Path.Combine(frameworkDir, name)))
                            continue;
                        var modulemap = Path.Combine(frameworkDir, "Modules", "module.modulemap");
                        if (File.Exists(modulemap))
                            AddFrom(modulemap);
                    }
                }
            }

            return frameworks.Count == 0 && libraries.Count == 0 ? Autolinks.None : new Autolinks(frameworks, libraries);
        }

        // `link "name"` and `link framework "Name"`, in any (sub)module. Comments are removed first so a
        // commented-out declaration never links.
        private static readonly Regex s_linkDecl = new(
            @"\blink\s+(?<fw>framework\s+)?""(?<name>[^""]+)""", RegexOptions.CultureInvariant);
        private static readonly Regex s_comments = new(
            @"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <summary>Reads the <c>link</c> declarations of a clang module map, in declaration order.</summary>
        internal static Autolinks ParseModuleMapLinks(string modulemapText)
        {
            var frameworks = new List<string>();
            var libraries = new List<string>();
            var text = s_comments.Replace(modulemapText, " ");
            foreach (Match m in s_linkDecl.Matches(text))
            {
                var name = m.Groups["name"].Value.Trim();
                if (name.Length == 0)
                    continue;
                var list = m.Groups["fw"].Success ? frameworks : libraries;
                if (!list.Contains(name, StringComparer.Ordinal))
                    list.Add(name);
            }
            return new Autolinks(frameworks, libraries);
        }

        /// <summary>
        /// Whether the SDK ships a <paramref name="name"/>.framework any client may link: one with a
        /// <c>.tbd</c> stub or a binary (some SDK frameworks are headers only), whose stub does not
        /// restrict its clients (ld rejects a direct link to a framework with an allowable-clients list).
        /// A non-null <paramref name="iOSSupportFrameworks"/> (Mac Catalyst) is searched as well.
        /// </summary>
        internal static bool SdkProvidesFramework(string? sdkPath, string name, string? iOSSupportFrameworks = null)
        {
            if (string.IsNullOrEmpty(sdkPath) || !IsPlainName(name))
                return false;
            static bool Linkable(string frameworksDir, string name)
            {
                var framework = Path.Combine(frameworksDir, name + ".framework");
                var stub = Path.Combine(framework, name + ".tbd");
                if (File.Exists(stub))
                    return !StubRestrictsClients(stub);
                return File.Exists(Path.Combine(framework, name));
            }
            return Linkable(Path.Combine(sdkPath, "System", "Library", "Frameworks"), name)
                || (iOSSupportFrameworks != null && Linkable(iOSSupportFrameworks, name));
        }

        /// <summary>
        /// Whether the SDK ships <c>lib<paramref name="name"/></c> under <c>usr/lib</c> for any client to
        /// link (a stub listing allowable clients is refused by ld, as for frameworks).
        /// </summary>
        internal static bool SdkProvidesLibrary(string? sdkPath, string name)
        {
            if (string.IsNullOrEmpty(sdkPath) || !IsPlainName(name))
                return false;
            var lib = Path.Combine(sdkPath, "usr", "lib", "lib" + name);
            if (File.Exists(lib + ".tbd"))
                return !StubRestrictsClients(lib + ".tbd");
            return File.Exists(lib + ".dylib") || File.Exists(lib + ".a");
        }

        // A text stub's main library (its first YAML document, or `main_library` in the JSON form) names
        // the only clients allowed to link it directly. Re-exported sub-libraries in later documents may
        // carry their own lists without restricting the framework itself.
        internal static bool StubRestrictsClients(string stubPath)
        {
            try
            {
                var text = File.ReadAllText(stubPath);
                if (text.TrimStart().StartsWith('{'))
                {
                    using var json = System.Text.Json.JsonDocument.Parse(text);
                    return json.RootElement.TryGetProperty("main_library", out var main)
                        && main.TryGetProperty("allowable_clients", out _);
                }
                using var reader = new StringReader(text);
                var first = true;
                for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
                {
                    if (first) { first = false; continue; } // the document's own `--- !tapi-tbd` header
                    if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("...", StringComparison.Ordinal))
                        return false;
                    if (line.StartsWith("allowable-clients:", StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Text.Json.JsonException) { return false; }
        }

        // A link name is a single file-name token; anything with a path separator or a flag prefix is not
        // a module-map link name and is never put on a command line.
        private static bool IsPlainName(string name) =>
            name.Length > 0
            && name.IndexOfAny(new[] { '/', '\\', '"', ' ' }) < 0
            && !name.StartsWith("-", StringComparison.Ordinal);

        private static IEnumerable<string> EnumerateModuleMaps(string headersDir)
        {
            if (!Directory.Exists(headersDir))
                return Array.Empty<string>();
            try
            {
                return Directory.EnumerateFiles(headersDir, "module.modulemap", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
            }
            catch (IOException) { return Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        private sealed record LibrarySlice(string SliceDir, string? Binary, string HeadersPath);

        // Each search path that is a library-type slice of an xcframework, with its linkable binary.
        private static IEnumerable<LibrarySlice> EnumerateSlices(IReadOnlyList<string>? searchPaths)
        {
            if (searchPaths == null)
                yield break;
            foreach (var path in searchPaths)
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    continue;
                var sliceDir = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                var xcframework = Path.GetDirectoryName(sliceDir);
                if (xcframework == null
                    || !xcframework.EndsWith(".xcframework", StringComparison.OrdinalIgnoreCase))
                    continue;
                var plist = Path.Combine(xcframework, "Info.plist");
                if (!File.Exists(plist))
                    continue;
                List<XCFrameworkSlice> slices;
                try { slices = XCFrameworkResolver.ParseInfoPlist(plist, null, NullLogger.Instance); }
                catch (Exception) { continue; }
                var id = Path.GetFileName(sliceDir);
                var slice = slices.FirstOrDefault(s => string.Equals(s.LibraryIdentifier, id, StringComparison.Ordinal));
                if (slice == null
                    || string.IsNullOrEmpty(slice.LibraryPath)
                    || slice.LibraryPath.EndsWith(".framework", StringComparison.Ordinal))
                    continue;
                var binary = Path.Combine(sliceDir, slice.LibraryPath);
                yield return new LibrarySlice(
                    sliceDir,
                    SwiftWrapperCompiler.IsLinkableBinary(binary) ? binary : null,
                    slice.HeadersPath);
            }
        }

        private static bool IsStaticArchive(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;
                using var stream = File.OpenRead(path);
                Span<byte> magic = stackalloc byte[8];
                var read = stream.Read(magic);
                if (read >= 8 && magic.SequenceEqual("!<arch>\n"u8))
                    return true;
                // A universal binary may wrap archives; lipo-free check: an archive member follows a fat
                // header, so read the first architecture's offset and look there. The fat header and its
                // arch records are big-endian; FAT_MAGIC_64 records carry a 64-bit offset.
                if (read >= 8 && magic[0] == 0xCA && magic[1] == 0xFE && magic[2] == 0xBA
                    && (magic[3] == 0xBE || magic[3] == 0xBF))
                {
                    var fat64 = magic[3] == 0xBF;
                    Span<byte> arch = stackalloc byte[fat64 ? 32 : 20];
                    if (stream.Read(arch) < arch.Length)
                        return false;
                    var offset = fat64
                        ? (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(arch.Slice(8, 8))
                        : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(arch.Slice(8, 4));
                    if (offset <= 0 || offset >= stream.Length)
                        return false;
                    stream.Position = offset;
                    Span<byte> inner = stackalloc byte[8];
                    return stream.Read(inner) >= 8 && inner.SequenceEqual("!<arch>\n"u8);
                }
                return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
