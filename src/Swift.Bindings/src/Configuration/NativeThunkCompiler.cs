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
            IReadOnlyList<string>? forceLoadBinaries = null)
        {
            var objectArgs = string.Join(" ", objectFiles.Select(f => $"\"{f}\""));

            // Add framework search path and link against the original framework
            // so the linker can resolve thunk bl instructions targeting Swift symbols.
            var frameworkFlags = "";
            if (!string.IsNullOrEmpty(frameworkSearchPath))
                frameworkFlags += $"-F \"{frameworkSearchPath}\" ";
            if (!string.IsNullOrEmpty(originalModuleName))
                frameworkFlags += $"-framework {originalModuleName} ";

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
                var errorPreview = stderr.Length > 2000 ? stderr.Substring(0, 2000) + "..." : stderr;
                throw new InvalidOperationException(
                    $"Thunk linking failed (exit code {exitCode}): {errorPreview}");
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
