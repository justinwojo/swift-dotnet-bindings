// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BindingsGeneration
{
    /// <summary>
    /// Command-line tool for generating C# bindings from Swift ABI files.
    /// </summary>
    public class BindingsGenerator
    {
        private const string DefaultConfigFileName = ".swiftbindings.json";

        /// <summary>
        /// Main entry point of the bindings generator tool.
        /// </summary>
        public static void Main(string[] args)
        {
            Option<string> swiftAbiOption = new(aliases: new[] { "-a", "--swiftabi" }, "Path to the Swift ABI file.") { IsRequired = true };
            Option<string> dylibOption = new(aliases: new[] { "-d", "--dylib" }, "Path to the dynamic library.") { IsRequired = true };
            Option<string> tbdOption = new(aliases: new[] { "-t", "--tbd" }, "Path to the TBD file.") { IsRequired = true };
            Option<string> outputDirectoryOption = new(aliases: new[] { "-o", "--output" }, "Output directory for generated bindings.") { IsRequired = true };
            Option<string> libraryNameOption = new(
                aliases: new[] { "-l", "--library-name" },
                description: "Runtime library name for DllImport. If not specified, uses the dylib path. " +
                             "Note: If the name starts with '@' (e.g., @rpath/...), escape it with backslash: '\\@rpath/Nuke.framework/Nuke'");
            Option<string> asyncLibraryOption = new(
                aliases: new[] { "--async-library" },
                description: "Library name for async wrapper functions. If not specified, uses the module library. " +
                             "Typically 'SwiftBindings' when using a separate wrapper library.");
            Option<string> namespacePatternOption = new(
                aliases: new[] { "--namespace-pattern" },
                description: "C# namespace pattern for generated modules and types. Supports {Module} and {Framework}. Default: Swift.{Module}");
            Option<string> swiftInterfaceOption = new(
                aliases: new[] { "-s", "--swiftinterface" },
                description: "Path to the .swiftinterface file. Used to detect @inlinable internal members " +
                             "that can't be distinguished from public in the ABI JSON alone.");
            Option<string> bridgeHintsOption = new(
                aliases: new[] { "--bridge-hints" },
                description: "Path to bridge hints JSON file for customizing SwiftUI bridge generation.");
            Option<string> configOption = new(
                aliases: new[] { "--config" },
                description: $"Path to config JSON file. Default: {DefaultConfigFileName} in current directory.");
            Option<int> verboseOption = new(
                aliases: new[] { "-v", "--verbose" },
                description: "Verbosity level. 0 = No logging, 1 = General information, 2 = Debugging information. (default: 1)",
                getDefaultValue: () => 1);
            Option<bool> helpOption = new(aliases: new[] { "-h", "--help" }, "Display a help message.");

            RootCommand rootCommand = new(description: "Swift bindings generator.")
            {
                swiftAbiOption,
                dylibOption,
                tbdOption,
                outputDirectoryOption,
                libraryNameOption,
                asyncLibraryOption,
                swiftInterfaceOption,
                bridgeHintsOption,
                namespacePatternOption,
                configOption,
                verboseOption,
                helpOption,
            };
            rootCommand.SetHandler((InvocationContext context) =>
            {
                var parseResult = context.ParseResult;
                var swiftAbiPath = parseResult.GetValueForOption(swiftAbiOption);
                var dylibPath = parseResult.GetValueForOption(dylibOption);
                var tbdPath = parseResult.GetValueForOption(tbdOption);
                var outputDirectory = parseResult.GetValueForOption(outputDirectoryOption);
                var libraryName = parseResult.GetValueForOption(libraryNameOption);
                var asyncLibrary = parseResult.GetValueForOption(asyncLibraryOption);
                var swiftInterface = parseResult.GetValueForOption(swiftInterfaceOption);
                var bridgeHints = parseResult.GetValueForOption(bridgeHintsOption);
                var namespacePattern = parseResult.GetValueForOption(namespacePatternOption);
                var configPath = parseResult.GetValueForOption(configOption);
                var verbose = parseResult.GetValueForOption(verboseOption);
                var help = parseResult.GetValueForOption(helpOption);

                if (help)
                {
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  -a, --swiftabi       Required. Path to the Swift ABI file.");
                    Console.WriteLine("  -d, --dylib          Required. Path to the dynamic library.");
                    Console.WriteLine("  -t, --tbd            Required. Path to the TBD file.");
                    Console.WriteLine("  -o, --output         Required. Output directory for generated bindings.");
                    Console.WriteLine("  -l, --library-name   Optional. Runtime library name for DllImport. Escape @ with backslash: '\\@rpath/...'");
                    Console.WriteLine("  --async-library      Optional. Library name for async wrapper functions. Default uses module library.");
                    Console.WriteLine("  -s, --swiftinterface Optional. Path to .swiftinterface file for internal member detection.");
                    Console.WriteLine("  --bridge-hints       Optional. Path to bridge hints JSON file for customizing SwiftUI bridge generation.");
                    Console.WriteLine($"  --namespace-pattern  Optional. Namespace pattern using {{Module}} and {{Framework}}. Default: {NamespacePatternResolver.DefaultPattern}");
                    Console.WriteLine($"  --config             Optional. Path to config file. Default: {DefaultConfigFileName}");
                    Console.WriteLine("  -v, --verbose        Verbosity level. 0 = No logging, 1 = General information, 2 = Debugging information. (default: 1)");
                    return;
                }

                ILoggerFactory loggerFactory = CreateLoggerFactory(verbose);
                ILogger logger = loggerFactory.CreateLogger<BindingsGenerator>();

                if (string.IsNullOrWhiteSpace(swiftAbiPath) || !File.Exists(swiftAbiPath))
                {
                    logger.LogError("Error: Valid Swift ABI file is required.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(dylibPath) || !File.Exists(dylibPath))
                {
                    logger.LogError("Error: Valid dynamic library is required.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(tbdPath) || !File.Exists(tbdPath))
                {
                    logger.LogError("Error: Valid TBD file is required.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
                {
                    logger.LogError("Error: Valid output directory is required.");
                    return;
                }

                // Use the provided library name, or fall back to the dylib path
                var runtimeLibraryName = string.IsNullOrWhiteSpace(libraryName) ? dylibPath : libraryName;
                var effectiveNamespacePattern = ResolveNamespacePattern(namespacePattern, configPath, logger);

                GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibrary, swiftInterface, bridgeHints, effectiveNamespacePattern, logger, loggerFactory);
            });

            rootCommand.Invoke(args);
        }

        /// <summary>
        /// Generates C# bindings from Swift ABI files.
        /// </summary>
        /// <param name="swiftAbiPath">Path to the Swift ABI file.</param>
        /// <param name="dylibPath">Path to the dynamic library (used for metadata extraction).</param>
        /// <param name="tbdPath">Path to the TBD file.</param>
        /// <param name="outputDirectory">Output directory for generated bindings.</param>
        /// <param name="runtimeLibraryName">Library name for DllImport in generated code.</param>
        /// <param name="asyncLibraryName">Library name for async wrapper functions. If null, uses module library.</param>
        /// <param name="namespacePattern">Namespace pattern for generated modules and types.</param>
        /// <param name="logger">ILogger instance.</param>
        /// <param name="loggerFactory">ILoggerFactory instance.</param>
        public static void GenerateBindings(string swiftAbiPath, string dylibPath, string tbdPath, string outputDirectory, string runtimeLibraryName, string? asyncLibraryName, string? swiftInterfacePath, string? bridgeHintsPath, string namespacePattern, ILogger logger, ILoggerFactory loggerFactory)
        {
            var typeDatabase = new TypeDatabase();
            typeDatabase.AsyncLibraryName = asyncLibraryName;
            string[] moduleDatabases = { "FoundationDatabase.xml", "SwiftDatabase.xml", "CoreGraphicsDatabase.xml", "DispatchDatabase.xml", "AppKitDatabase.xml", "CoreImageDatabase.xml", "UIKitDatabase.xml" };
            foreach (var database in moduleDatabases)
            {
                typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", database)).Wait();
            }

            logger.LogInformation("Starting bindings generation for {SwiftAbiPath}...", swiftAbiPath);
            logger.LogInformation("Runtime library name: {LibraryName}", runtimeLibraryName);

            // Parse the TBD file
            Demangling.DemanglingResults demangledTbdFile = Demangling.DemanglingResults.FromTbd(tbdPath, loggerFactory);

            // Parse swiftinterface for internal member detection (supplementary data)
            HashSet<string>? internalMemberKeys = null;
            if (!string.IsNullOrWhiteSpace(swiftInterfacePath) && File.Exists(swiftInterfacePath))
            {
                internalMemberKeys = SwiftInterfaceAccessParser.GetInternalMembers(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} internal member keys from swiftinterface", internalMemberKeys.Count);
            }

            // Initialize the Swift ABI parser
            var swiftParser = new SwiftABIParser(swiftAbiPath, typeDatabase, demangledTbdFile, loggerFactory.CreateLogger<SwiftABIParser>(), internalMemberKeys);
            var moduleName = swiftParser.GetModuleName();
            var frameworkName = InferFrameworkName(dylibPath, moduleName);
            var namespaceResolver = new NamespacePatternResolver(namespacePattern, frameworkName);

            // Skip if the module has already been processed
            // Modules will have to be processed in topological order
            if (!typeDatabase.IsModuleProcessed(moduleName))
            {
                // Parse the Swift ABI file and generate declarations
                var (decl, moduleTypes) = swiftParser.ParseModule();
                ReportCollector.Start(decl);

                // dylibPath is used for metadata extraction, runtimeLibraryName is used in generated DllImport
                var moduleProcessor = new ModuleProcessor(moduleName, dylibPath, runtimeLibraryName, moduleTypes, typeDatabase, loggerFactory.CreateLogger<ModuleProcessor>(), namespaceResolver);
                var moduleDatabase = moduleProcessor.FinalizeTypeProcessingAndCreateModuleDatabase().ModuleDatabase;
                typeDatabase.AddModuleDatabase(moduleDatabase);

                logger.LogDebug("Parsed Swift ABI file successfully.");

                // Emit the C# bindings
                var stringEmitter = new StringEmitter(outputDirectory, typeDatabase, loggerFactory, namespaceResolver, bridgeHintsPath);
                stringEmitter.EmitModule(decl);

                var report = ReportCollector.Complete();
                if (report != null)
                {
                    ReportEmitter.Emit(report, outputDirectory, logger);
                }
                ReportCollector.Reset();

                logger.LogInformation("Bindings generation completed for {SwiftAbiPath}.", swiftAbiPath);

            }
            else
                logger.LogWarning("Bindings generation already completed for {SwiftAbiPath}.", swiftAbiPath);

            // Copy the Swift library to the output directory
            CopyDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift"), Path.Combine(outputDirectory, "Swift"), true);
        }

        private static string ResolveNamespacePattern(string? cliNamespacePattern, string? configPath, ILogger logger)
        {
            if (!string.IsNullOrWhiteSpace(cliNamespacePattern))
            {
                return cliNamespacePattern;
            }

            string resolvedConfigPath = string.IsNullOrWhiteSpace(configPath)
                ? Path.Combine(Environment.CurrentDirectory, DefaultConfigFileName)
                : configPath;

            if (!File.Exists(resolvedConfigPath))
            {
                return NamespacePatternResolver.DefaultPattern;
            }

            try
            {
                var configText = File.ReadAllText(resolvedConfigPath);
                var config = JObject.Parse(configText);
                var configNamespacePattern = config.Value<string>("namespacePattern");
                if (!string.IsNullOrWhiteSpace(configNamespacePattern))
                {
                    return configNamespacePattern;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse config file {ConfigPath}. Using default namespace pattern.", resolvedConfigPath);
            }

            return NamespacePatternResolver.DefaultPattern;
        }

        private static string InferFrameworkName(string dylibPath, string moduleName)
        {
            if (string.IsNullOrWhiteSpace(dylibPath))
            {
                return moduleName;
            }

            var pathSegments = dylibPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in pathSegments)
            {
                if (segment.EndsWith(".framework", StringComparison.OrdinalIgnoreCase))
                {
                    var frameworkName = Path.GetFileNameWithoutExtension(segment);
                    if (!string.IsNullOrWhiteSpace(frameworkName))
                    {
                        return frameworkName;
                    }
                }
            }

            var fileName = Path.GetFileNameWithoutExtension(dylibPath);
            return string.IsNullOrWhiteSpace(fileName) ? moduleName : fileName;
        }

        static void CopyDirectory(string sourceDir, string destinationDir, bool recursive)
        {
            // Get information about the source directory
            var dir = new DirectoryInfo(sourceDir);

            // Check if the source directory exists
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            // Cache directories before we start copying
            DirectoryInfo[] dirs = dir.GetDirectories();

            // Create the destination directory
            Directory.CreateDirectory(destinationDir);

            // Get the files in the source directory and copy to the destination directory
            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath, true);
            }

            // If recursive and copying subdirectories, recursively call this method
            if (recursive)
            {
                foreach (DirectoryInfo subDir in dirs)
                {
                    string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                    CopyDirectory(subDir.FullName, newDestinationDir, true);
                }
            }
        }

        /// <summary>
        /// Creates and configures a logger factory based on the verbosity level.
        /// </summary>
        /// <param name="verbosity">Verbosity level (0 = No logging, 1 = General information, 2 = Debugging information).</param>
        static ILoggerFactory CreateLoggerFactory(int verbosity)
        {
            return LoggerFactory.Create(builder =>
            {
                builder.AddConsole();

                builder.SetMinimumLevel(verbosity switch
                {
                    0 => LogLevel.None,  // No logging
                    1 => LogLevel.Information, // Info and above
                    2 => LogLevel.Debug,    // Debug and above
                    _ => throw new ArgumentOutOfRangeException(nameof(verbosity), "Invalid verbosity level.")
                });
            });
        }
    }
}
