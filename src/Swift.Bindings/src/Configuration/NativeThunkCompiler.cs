// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Result of compiling native ARM64 thunk assembly files.
    /// </summary>
    public sealed class NativeThunkCompilationResult
    {
        /// <summary>
        /// Paths to the compiled .o object files.
        /// </summary>
        public required IReadOnlyList<string> ObjectFiles { get; init; }

        /// <summary>
        /// Number of .arm64.s files that were compiled.
        /// </summary>
        public int CompiledFileCount => ObjectFiles.Count;
    }

    /// <summary>
    /// Compiles generated ARM64 assembly thunk files (.arm64.s) into object files (.o).
    /// Does NOT link — the resulting .o files are passed to SwiftWrapperCompiler
    /// for linking into the wrapper xcframework binary.
    /// </summary>
    public static class NativeThunkCompiler
    {
        /// <summary>
        /// Compiles all <c>.{arch}.s</c> files in the output directory into .o object files.
        /// Returns null if no matching assembly files exist.
        /// </summary>
        /// <param name="outputDirectory">Directory containing generated <c>.{arch}.s</c> files.</param>
        /// <param name="targetTriple">Target triple (e.g., "arm64-apple-ios17.0-simulator").</param>
        /// <param name="sdkPath">Resolved SDK path from xcrun.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        /// <param name="arch">Architecture tag selecting the assembly files to compile ("arm64" or "x86_64").</param>
        public static NativeThunkCompilationResult? CompileThunkObjects(
            string outputDirectory,
            string targetTriple,
            string sdkPath,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            string arch = "arm64")
        {
            var assemblyFiles = CollectAssemblyFiles(outputDirectory, arch);
            if (assemblyFiles.Count == 0)
            {
                logger.LogDebug("No .{Arch}.s thunk files found in {Dir} — skipping thunk compilation.", arch, outputDirectory);
                return null;
            }

            commandRunner ??= new SystemCommandRunner();
            var objectFiles = new List<string>();

            logger.LogInformation("Compiling {Count} native thunk assembly file(s)...", assemblyFiles.Count);

            foreach (var asmFile in assemblyFiles)
            {
                var objectFile = Path.ChangeExtension(asmFile, ".o");
                CompileAssemblyFile(asmFile, objectFile, targetTriple, sdkPath, commandRunner, logger);
                objectFiles.Add(objectFile);
            }

            logger.LogInformation("Compiled {Count} thunk object file(s).", objectFiles.Count);

            return new NativeThunkCompilationResult
            {
                ObjectFiles = objectFiles
            };
        }

        /// <summary>
        /// Links thunk .o files into a shared library using clang.
        /// Used when there are NO Swift wrapper files (all functions were thunked),
        /// so swiftc cannot be used (it requires at least one .swift input).
        /// </summary>
        /// <param name="objectFiles">Compiled .o files to link.</param>
        /// <param name="outputBinaryPath">Path for the output dylib.</param>
        /// <param name="wrapperModuleName">Module name for the install_name.</param>
        /// <param name="targetTriple">Target triple.</param>
        /// <param name="sdkPath">SDK path.</param>
        /// <param name="commandRunner">Command runner.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="frameworkSearchPath">Search path for the original framework.</param>
        /// <param name="originalModuleName">Module name for -framework linking.</param>
        /// <param name="forceLoadBinaries">Static-archive primaries to force-load into the wrapper.</param>
        /// <param name="linkFrameworks">Author-declared system frameworks (--link-framework) to link.</param>
        /// <param name="linkLibraries">Author-declared system libraries (--link-library) to link.</param>
        /// <param name="transitiveFrameworks">
        /// Sibling framework-dependency names (pre-scanned by the caller from the genuine
        /// user-passed <c>--framework-dependency</c> search paths — NOT the internally-injected
        /// XCTest platform path) to emit as <c>-framework</c>. Mirrors the swiftc path's
        /// transitive framework flags (which scan the same raw set) so a thunk-only wrapper that
        /// references a companion framework's symbols carries the matching load commands.
        /// </param>
        /// <param name="transitiveFrameworkSearchPaths">
        /// The raw dependency search-path dirs the caller scanned to produce
        /// <paramref name="transitiveFrameworks"/>. Each is emitted as a <c>-F</c> so the linker
        /// can LOCATE those <c>-framework</c> names — a companion lives in a dep dir, not the
        /// primary source slice's <paramref name="frameworkSearchPath"/>, so without these the
        /// emitted <c>-framework Dep</c> would fail with ld "framework not found". Mirrors the
        /// swiftc path, which emits a <c>-F</c> for every effective dependency search path.
        /// </param>
        /// <param name="buildLinkFailureHint">
        /// Maps the linker stderr to actionable system-link guidance appended to the failure
        /// message (single source of truth: <c>SwiftWrapperCompiler.BuildSystemLinkDependencyHint</c>),
        /// so an undeclared system-framework/libc++ link failure on this path points at
        /// <c>--link-framework</c>/<c>&lt;SwiftLinkFramework&gt;</c> (or the library-only
        /// <c>--link-library</c>/<c>&lt;SwiftLinkLibrary&gt;</c> form) instead of an opaque wall.
        /// </param>
        internal static void LinkWithClang(
            IReadOnlyList<string> objectFiles,
            string outputBinaryPath,
            string wrapperModuleName,
            string targetTriple,
            string sdkPath,
            ICommandRunner commandRunner,
            ILogger logger,
            string? frameworkSearchPath = null,
            string? originalModuleName = null,
            IReadOnlyList<string>? forceLoadBinaries = null,
            IReadOnlyList<string>? linkFrameworks = null,
            IReadOnlyList<string>? linkLibraries = null,
            IReadOnlyList<string>? transitiveFrameworks = null,
            Func<string, string>? buildLinkFailureHint = null,
            IReadOnlyList<string>? transitiveFrameworkSearchPaths = null)
        {
            var objectArgs = string.Join(" ", objectFiles.Select(f => $"\"{f}\""));

            // Add framework search path and link against the original framework
            // so the linker can resolve thunk bl instructions targeting Swift symbols.
            // `seenFrameworks` is shared across the primary, transitive, and author-declared
            // `-framework` emissions so a name is never emitted twice (mirrors the swiftc path's
            // single `seenLinkedFrameworks`); seed it with the primary module up front so a
            // declared --link-framework matching the module name does not duplicate it.
            var frameworkFlags = "";
            var seenFrameworks = new HashSet<string>(StringComparer.Ordinal);
            var seenSearchPaths = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(frameworkSearchPath))
            {
                frameworkFlags += $"-F \"{frameworkSearchPath}\" ";
                seenSearchPaths.Add(frameworkSearchPath);
            }
            if (!string.IsNullOrEmpty(originalModuleName))
            {
                frameworkFlags += $"-framework {originalModuleName} ";
                seenFrameworks.Add(originalModuleName);
            }

            // `-F` for every transitive dependency search-path dir so the linker can LOCATE the
            // `-framework` names emitted below. The companions scanned into transitiveFrameworks
            // live in these dep dirs, NOT the primary source slice's frameworkSearchPath; without
            // a matching `-F`, `-framework Dep` fails with ld "framework not found". Mirrors the
            // swiftc path, which emits `-F` for every effective dependency search path. Emitted
            // unconditionally per dir (an extra `-F` over a dir with no framework is harmless),
            // deduped against the primary path and each other.
            if (transitiveFrameworkSearchPaths != null)
            {
                foreach (var path in transitiveFrameworkSearchPaths)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (seenSearchPaths.Add(path))
                        frameworkFlags += $"-F \"{path}\" ";
                }
            }

            // Transitive `-framework` flags for sibling framework-dependency xcframeworks, scanned
            // by the caller from the genuine user-passed `--framework-dependency` search paths (the
            // same raw set the swiftc path scans, excluding the internally-injected XCTest platform
            // path). A thunk-only wrapper that references a companion framework's symbols needs these
            // load commands just as the swiftc path emits them via transitiveFrameworkLinkerFlags.
            if (transitiveFrameworks != null)
            {
                foreach (var fw in transitiveFrameworks)
                {
                    if (string.IsNullOrWhiteSpace(fw)) continue;
                    var name = fw.Trim();
                    if (seenFrameworks.Add(name))
                        frameworkFlags += $"-framework {name} ";
                }
            }

            // Gap 2: force-load a static-archive primary so this thunk-only wrapper carries
            // the framework's ObjC classes (a bare `-framework` against a static archive
            // pulls only lazily-referenced members). Same rationale as the swiftc path.
            if (forceLoadBinaries != null)
            {
                foreach (var binary in forceLoadBinaries)
                {
                    if (!string.IsNullOrEmpty(binary) && File.Exists(binary))
                        frameworkFlags += $"-Wl,-force_load,\"{binary}\" ";
                }
            }

            // Author-declared link inputs (--link-framework / --link-library, surfaced by the SDK
            // as <SwiftLinkFramework> / <SwiftLinkLibrary>). A force-loaded static-archive source
            // drags in objects that reference Apple system frameworks (CoreVideo, Metal, OpenGLES,
            // Accelerate, …) and libc++ with no autolink hints, so the author must declare them;
            // here they become real clang `-framework`/`-l` flags so the wrapper dylib carries the
            // matching LC_LOAD_DYLIB load commands. Mirrors InvokeSwiftCompiler's explicitLinkFlags
            // so the thunk-only wrapper (no .swift inputs) gets the same linkage as the swiftc path.
            if (linkFrameworks != null)
            {
                foreach (var fw in linkFrameworks)
                {
                    if (string.IsNullOrWhiteSpace(fw)) continue;
                    var name = fw.Trim();
                    if (seenFrameworks.Add(name))
                        frameworkFlags += $"-framework {name} ";
                }
            }
            if (linkLibraries != null)
            {
                var seenLibraries = new HashSet<string>(StringComparer.Ordinal);
                foreach (var lib in linkLibraries)
                {
                    if (string.IsNullOrWhiteSpace(lib)) continue;
                    // Accept bare names ("c++") or already-prefixed ("-lc++"); normalize to -l<name>.
                    var name = lib.Trim();
                    if (name.StartsWith("-l", StringComparison.Ordinal))
                        name = name.Substring(2);
                    if (name.Length > 0 && seenLibraries.Add(name))
                        frameworkFlags += $"-l{name} ";
                }
            }

            var args = $"clang -shared -target {targetTriple} " +
                       $"-isysroot \"{sdkPath}\" " +
                       $"{frameworkFlags}" +
                       $"-install_name @rpath/{wrapperModuleName}.framework/{wrapperModuleName} " +
                       $"-o \"{outputBinaryPath}\" " +
                       objectArgs;

            logger.LogDebug("Invoking: xcrun {Args}", args);

            var (exitCode, stdout, stderr) = commandRunner.Run("xcrun", args, timeoutMs: 60000);

            if (exitCode != 0)
            {
                // Surface the same actionable system-link guidance the swiftc path emits when an
                // undeclared system framework / libc++ dependency of a force-loaded static archive
                // leaves undefined symbols. Computed on the FULL stderr (before truncation) so a
                // needle past the 2000-char preview boundary is still detected.
                var hint = buildLinkFailureHint?.Invoke(stderr) ?? string.Empty;
                var errorPreview = stderr.Length > 2000 ? stderr.Substring(0, 2000) + "..." : stderr;
                throw new InvalidOperationException(
                    $"Thunk linking failed (exit code {exitCode}): {errorPreview}{hint}");
            }
        }

        /// <summary>
        /// Collects <c>.{arch}.s</c> assembly files from the output directory.
        /// </summary>
        /// <param name="outputDirectory">Directory to search.</param>
        /// <param name="arch">Architecture tag ("arm64" or "x86_64"). Defaults to "arm64".</param>
        internal static List<string> CollectAssemblyFiles(string outputDirectory, string arch = "arm64")
        {
            if (!Directory.Exists(outputDirectory))
                return new List<string>();

            return Directory.GetFiles(outputDirectory, $"*.{arch}.s")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Compiles a single .arm64.s file to a .o object file using xcrun clang.
        /// </summary>
        private static void CompileAssemblyFile(
            string assemblyFile,
            string objectFile,
            string targetTriple,
            string sdkPath,
            ICommandRunner commandRunner,
            ILogger logger)
        {
            var args = $"clang -c \"{assemblyFile}\" -o \"{objectFile}\" " +
                       $"-target {targetTriple} -isysroot \"{sdkPath}\"";

            logger.LogDebug("Invoking: xcrun {Args}", args);

            var (exitCode, stdout, stderr) = commandRunner.Run("xcrun", args, timeoutMs: 60000);

            if (exitCode != 0)
            {
                var errorPreview = stderr.Length > 2000 ? stderr.Substring(0, 2000) + "..." : stderr;
                throw new InvalidOperationException(
                    $"Thunk assembly compilation failed for '{Path.GetFileName(assemblyFile)}' " +
                    $"(exit code {exitCode}): {errorPreview}");
            }

            logger.LogDebug("Compiled thunk: {Source} -> {Object}",
                Path.GetFileName(assemblyFile), Path.GetFileName(objectFile));
        }
    }
}
