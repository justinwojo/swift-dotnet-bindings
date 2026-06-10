// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CommandLine;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using BindingsGeneration.ObjC;

[assembly: InternalsVisibleTo("Swift.Bindings.Unit.Tests")]

namespace BindingsGeneration
{
    /// <summary>
    /// Command-line tool for generating C# bindings from Swift ABI files.
    /// Contains the entry point, public API, and utility methods used by
    /// <see cref="BindingsGeneratorCommand"/> for the CLI handler pipeline.
    /// </summary>
    public class BindingsGenerator
    {
        internal const string DefaultConfigFileName = ".swiftbindings.json";

        /// <summary>
        /// Main entry point of the bindings generator tool.
        /// </summary>
        public static int Main(string[] args)
        {
            var options = new CliOptions();
            var rootCommand = options.CreateRootCommand();
            rootCommand.SetHandler(context => BindingsGeneratorCommand.Execute(context, options));
            return rootCommand.Invoke(args);
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
        public static void GenerateBindings(string swiftAbiPath, string dylibPath, string tbdPath, string outputDirectory, string runtimeLibraryName, string? asyncLibraryName, string? swiftInterfacePath, string? symbolGraphPath, string? bridgeHintsPath, string namespacePattern, ILogger logger, ILoggerFactory loggerFactory)
        {
            GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibraryName, swiftInterfacePath, symbolGraphPath, bridgeHintsPath, namespacePattern, logger, loggerFactory, out _, out _, out _, out _, dependencyModuleNames: null, moduleDatabasePaths: null);
        }

        internal static bool GenerateBindings(string swiftAbiPath, string dylibPath, string tbdPath, string outputDirectory, string runtimeLibraryName, string? asyncLibraryName, string? swiftInterfacePath, string? symbolGraphPath, string? bridgeHintsPath, string namespacePattern, ILogger logger, ILoggerFactory loggerFactory, out HashSet<string>? internalTypeNames, out string? moduleNameForCollision, out HashSet<string>? nestedTypesInCollidingClass, out DepModuleCollisionDetector.SlicedCollisionResult depModuleCollisions, List<string>? dependencyModuleNames = null, string[]? moduleDatabasePaths = null, List<FrameworkDependencyInfo>? resolvedDependencies = null, ApplePlatform? platform = null, bool keepBuiltinDatabaseForTargetModule = false, Producers.InterfaceFactsAggregator? factsAggregator = null)
        {
            internalTypeNames = null;
            moduleNameForCollision = null;
            nestedTypesInCollidingClass = null;
            depModuleCollisions = new DepModuleCollisionDetector.SlicedCollisionResult(
                Array.Empty<string>(), Array.Empty<string>());
            try
            {
            var typeDatabase = new TypeDatabase();
            typeDatabase.AsyncLibraryName = asyncLibraryName;

            // Peek at current module name once. Used by Apple-framework target mode
            // (skip a colliding built-in database below) and the --module-database /
            // --framework-dependency self-reference checks further down.
            string? currentModuleName = null;
            try
            {
                currentModuleName = PeekModuleNameFromAbiJson(swiftAbiPath);
            }
            catch
            {
                // Non-fatal: self-reference checks will be skipped
            }

            // Load a built-in dependency database, unless its module name collides
            // with the input abi.json's module name (Apple-framework target mode).
            //
            // The built-in *Database.xml stubs were authored as dependency-resolution
            // helpers for downstream third-party libraries that *reference* Apple
            // framework types. When the input abi.json IS the framework (e.g.,
            // generating real bindings for StoreKit), the pre-loaded stub collides
            // with the parse-and-emit gate (`IsModuleProcessed`) and the generator
            // silently skips the input. Auto-detect that case by peeking each
            // candidate database's moduleName and skipping the matching entry. This
            // can be disabled with --keep-builtin-database for the rare case where a
            // third-party Swift module shares a name with an Apple framework AND the
            // caller wants the legacy stub behavior.
            //
            // Follow-up (out of scope for this spike): TypeDatabase.IsModuleLoaded
            // and IsModuleProcessed are aliased today. Splitting them into distinct
            // predicates ("we have a dependency stub" vs "we have generated real
            // bindings") would let us keep the stub loaded even when the input is
            // the same module — a cleaner long-term shape than skip-and-replace.
            void LoadBuiltInDatabase(string database)
            {
                var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", database);
                if (currentModuleName != null && !keepBuiltinDatabaseForTargetModule)
                {
                    var dbModuleName = PeekModuleNameFromXml(dbPath);
                    if (dbModuleName != null && dbModuleName == currentModuleName)
                    {
                        logger.LogInformation(
                            "Apple-framework target mode: skipping built-in database '{Database}' because the input abi.json targets the same module '{Module}'. Pass --keep-builtin-database to disable this auto-detection.",
                            database, currentModuleName);
                        return;
                    }
                }
                typeDatabase.LoadModuleDatabaseFromFile(dbPath).Wait();
            }

            // Platform-aware database loading: skip databases for frameworks that are
            // entirely absent on the target platform. Unused entries are harmless (lookup-based),
            // but skipping them avoids spurious type resolution for unavailable frameworks.
            foreach (var database in GetBuiltInDatabases(platform))
            {
                LoadBuiltInDatabase(database);
            }

            // Load dependency module databases for cross-module type resolution
            if (moduleDatabasePaths != null)
            {
                foreach (var dbPath in moduleDatabasePaths)
                {
                    var dbModuleName = PeekModuleNameFromXml(dbPath);
                    if (dbModuleName == null)
                    {
                        logger.LogError("SWIFTBIND072: Invalid module database XML: '{Path}'.", dbPath);
                        return false;
                    }

                    if (currentModuleName != null && dbModuleName == currentModuleName)
                    {
                        logger.LogInformation("SWIFTBIND071: Skipping module database '{Path}' — it targets the current module '{Module}'.", dbPath, dbModuleName);
                        continue;
                    }

                    if (typeDatabase.IsModuleLoaded(dbModuleName))
                    {
                        logger.LogInformation("Module '{Module}' already loaded (built-in), skipping.", dbModuleName);
                        continue;
                    }

                    try
                    {
                        typeDatabase.LoadModuleDatabaseFromFile(dbPath).Wait();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("SWIFTBIND072: Failed to load module database '{Path}': {Message}", dbPath, ex.InnerException?.Message ?? ex.Message);
                        return false;
                    }
                    logger.LogInformation("Loaded dependency module database: {Path} (module: {Module})", dbPath, dbModuleName);
                }
            }

            // Accumulator for dependency-module ProtocolDecls. Threaded onto the bound
            // module's ModuleDecl after parse so EveryProtocol emission can flatten
            // cross-module parent witnesses into the child's vtable
            // (justinwojo/swift-dotnet-bindings#40 cross-module variant).
            var dependencyProtocols = new Dictionary<string, List<ProtocolDecl>>(StringComparer.Ordinal);

            // Load dependency type databases from framework dependency ABI JSON files.
            // This enables cross-module type resolution: dependency types resolve to concrete
            // projections instead of falling back to AnyType.
            if (resolvedDependencies != null)
            {
                foreach (var dep in resolvedDependencies)
                {
                    // Skip ObjC-only deps (no Swift ABI) and deps without ABI JSON
                    if (dep.IsObjCOnly || string.IsNullOrEmpty(dep.AbiJsonPath) || string.IsNullOrEmpty(dep.TbdPath))
                        continue;

                    // Skip self-reference
                    if (currentModuleName != null && dep.ModuleName == currentModuleName)
                        continue;

                    // Skip if already loaded (built-in XML or --module-database)
                    if (typeDatabase.IsModuleLoaded(dep.ModuleName))
                    {
                        logger.LogInformation("Dependency module '{Module}' already loaded, skipping ABI parse.", dep.ModuleName);
                        continue;
                    }

                    try
                    {
                        var depDemangledTbd = Demangling.DemanglingResults.FromTbd(dep.TbdPath, loggerFactory);
                        var depParser = new SwiftABIParser(
                            dep.AbiJsonPath, typeDatabase, depDemangledTbd,
                            loggerFactory.CreateLogger<SwiftABIParser>(),
                            SwiftInterfaceFacts.Empty);
                        var depModuleName = depParser.GetModuleName();
                        var depParseResult = depParser.ParseModule();

                        var depProcessor = new ModuleProcessor(
                            depModuleName, dep.DylibPath ?? dep.AbiJsonPath, dep.DylibPath ?? dep.AbiJsonPath,
                            depParseResult.TypeDecls, typeDatabase,
                            loggerFactory.CreateLogger<ModuleProcessor>());
                        var depModuleDb = depProcessor.FinalizeTypeProcessingAndCreateModuleDatabase().ModuleDatabase;
                        typeDatabase.AddModuleDatabase(depModuleDb);

                        // Apply nested-type rename pass to the dep module so cross-module
                        // references in the bound module's emit resolve to the renamed C# name
                        // (e.g., Parent.AlertType → Parent.AlertTypeType when the parent has a
                        // colliding property). Without this, dep TypeRecords keep the raw Swift
                        // leaf name and the consumer emits `Dep.Parent.AlertType` which C#
                        // resolves to the property rather than the type — CS0426.
                        // When the dep XML is pre-loaded via --module-database, the renamed
                        // managedTypeName is already in the XML; the ABI re-parse branch is the
                        // gap (BindingTests path uses --framework-dependency without --module-database).
                        NameProvider.PrecomputeNestedTypeRenames(depParseResult.ModuleDecl, typeDatabase);

                        // Retain the dependency's parsed ModuleDecl so consumer-side emitters can
                        // walk constructor shapes the TypeRecord projection discards (e.g. the
                        // KeyPath-init factory emitter needs a dep class's `init<G: P>(KeyPath<G, V>)`).
                        typeDatabase.AddDependencyModuleDecl(depParseResult.ModuleDecl);

                        // Stash dep ProtocolDecls so the bound module's EveryProtocol emission
                        // can resolve cross-module parents to their full member list.
                        if (depParseResult.ModuleDecl.Protocols is { Count: > 0 } depProtos)
                            dependencyProtocols[depModuleName] = depProtos;

                        logger.LogInformation("Loaded dependency types from ABI JSON: {Module}", depModuleName);
                    }
                    catch (Exception ex)
                    {
                        if (dep.IsAutoDetected)
                        {
                            // Auto-detected dependencies are best-effort — warn and continue
                            logger.LogWarning(
                                "Could not load dependency types for auto-detected module '{Module}': {Message}. " +
                                "Dependency types will resolve to AnyType.",
                                dep.ModuleName, ex.InnerException?.Message ?? ex.Message);
                        }
                        else
                        {
                            // Explicit --framework-dependency — fail hard (matches existing fail-fast behavior)
                            logger.LogError(
                                "SWIFTBIND073: Failed to parse dependency ABI for '{Module}': {Message}",
                                dep.ModuleName, ex.InnerException?.Message ?? ex.Message);
                            return false;
                        }
                    }
                }
            }

            logger.LogInformation("Starting bindings generation for {SwiftAbiPath}...", swiftAbiPath);
            logger.LogInformation("Runtime library name: {LibraryName}", runtimeLibraryName);

            // Parse the TBD file
            Demangling.DemanglingResults demangledTbdFile = Demangling.DemanglingResults.FromTbd(tbdPath, loggerFactory);

            // Parse swiftinterface into a single SwiftInterfaceFacts via the producer aggregator.
            // The default aggregator runs only the regex producer (existing behavior; per-fact
            // try/catch lives inside RegexInterfaceFactsProducer). Callers that pass a custom
            // aggregator (e.g. CLI flag --interface-facts-producer swift-syntax) get fact-by-fact
            // merging — see Producers/InterfaceFactsAggregator.cs for the merge rule.
            SwiftInterfaceFacts facts;
            if (!string.IsNullOrWhiteSpace(swiftInterfacePath) && File.Exists(swiftInterfacePath))
            {
                var aggregator = factsAggregator
                    ?? new Producers.InterfaceFactsAggregator(new[] { (Producers.IInterfaceFactsProducer)new Producers.RegexInterfaceFactsProducer() });
                facts = aggregator.Aggregate(swiftInterfacePath, logger);
            }
            else
            {
                facts = SwiftInterfaceFacts.Empty;
            }

            // Parse symbol graph for doc comments (supplementary data)
            Dictionary<string, DocComment>? docComments = null;
            if (!string.IsNullOrWhiteSpace(symbolGraphPath))
            {
                if (File.Exists(symbolGraphPath) || Directory.Exists(symbolGraphPath))
                {
                    docComments = SymbolGraphDocParser.ParseSymbolGraphs(symbolGraphPath);
                    logger.LogInformation("Loaded {Count} doc comments from symbol graph", docComments.Count);
                }
                else
                {
                    logger.LogWarning("Symbol graph path not found: {Path}. Doc comments will not be generated.", symbolGraphPath);
                }
            }

            // Initialize the Swift ABI parser
            var swiftParser = new SwiftABIParser(swiftAbiPath, typeDatabase, demangledTbdFile, loggerFactory.CreateLogger<SwiftABIParser>(), facts, docComments);
            var moduleName = swiftParser.GetModuleName();
            var frameworkName = InferFrameworkName(dylibPath, moduleName);
            var namespaceResolver = new NamespacePatternResolver(namespacePattern, frameworkName);

            // Skip if the module has already been processed
            // Modules will have to be processed in topological order
            if (!typeDatabase.IsModuleProcessed(moduleName))
            {
                // Parse the Swift ABI file and generate declarations
                var (decl, moduleTypes) = swiftParser.ParseModule();
                decl.ExportedSymbols = demangledTbdFile.AllSymbols;
                if (dependencyModuleNames != null)
                    decl.DependencyModuleNames = dependencyModuleNames;
                decl.DependencyProtocols = dependencyProtocols;
                // Thread the bound module's swiftinterface path so EmitSwiftImports can
                // intersect DependencyModuleNames with the real textual imports — without
                // this, the wrapper emits `import absl/grpc/...` for every sibling passed
                // via --framework-dependency, even when the bound source never uses them.
                if (!string.IsNullOrWhiteSpace(swiftInterfacePath) && File.Exists(swiftInterfacePath))
                    decl.SwiftInterfacePath = swiftInterfacePath;
                decl.InternalTypeNames = CollectInternalTypeNames(decl);
                internalTypeNames = decl.InternalTypeNames;

                // Detect module/type name collision: a public type whose name matches the module name.
                // When this occurs, Swift resolves bare "ModuleName" as the type, not the module,
                // causing "ModuleName.X" to fail (looks for X nested in the class, not in the module).
                var collidingType = decl.Types.FirstOrDefault(t => !t.IsModuleInternal && t.Name == moduleName);
                if (collidingType != null)
                {
                    moduleNameForCollision = moduleName;
                    logger.LogInformation("Detected module/type name collision: module '{Module}' has a public type with the same name. Will strip module prefixes in Swift wrapper.", moduleName);

                    // EC-18: Collect types nested inside the colliding class to prevent
                    // over-stripping. E.g., LoggingLib.Level should stay qualified
                    // because Level is nested in class LoggingLib, not a module-level type.
                    var nestedNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var innerType in collidingType.Types)
                    {
                        nestedNames.Add(innerType.Name);
                    }
                    if (nestedNames.Count > 0)
                    {
                        nestedTypesInCollidingClass = nestedNames;
                        logger.LogInformation("Found {Count} nested type(s) in colliding class '{Module}': {Types}",
                            nestedNames.Count, moduleName, string.Join(", ", nestedNames));
                    }
                }

                // Detect dep-module/type name collisions: a --framework-dependency whose
                // module name is also the name of a public type or @interface that the
                // dependency exports (e.g., GTMSessionFetcher.xcframework's GTMSessionFetcher
                // ObjC class collides with module GTMSessionFetcher when GTMAppAuth's
                // swiftinterface writes `GTMSessionFetcher.GTMSessionFetcherServiceProtocol`).
                // The detection result feeds SwiftWrapperCompiler.PrecompileCollidingModule
                // via the caller, which patches the bound interface to strip the
                // `<DepModule>.` qualifier and compiles a shadow binary swiftmodule.
                if (resolvedDependencies != null && resolvedDependencies.Count > 0 && platform != null)
                {
                    var detectorPlatformInfo = PlatformInfoFactory.Create(platform.Value);
                    depModuleCollisions = DepModuleCollisionDetector.DetectPerSlice(
                        resolvedDependencies, detectorPlatformInfo, logger);
                }

                // Wire publicTypeNames from swiftinterface as keep-override for underscore suppression.
                // publicTypeNames are dot-qualified (e.g., "_InternalType"); underscore suppression
                // uses module-qualified names (e.g., "Module._InternalType"). Normalize by prepending module.
                HashSet<string>? keepUnderscoreTypes = null;
                if (facts.PublicTypeNames.Count > 0)
                {
                    keepUnderscoreTypes = new HashSet<string>();
                    foreach (var name in facts.PublicTypeNames)
                    {
                        if (name.StartsWith("_") || name.Contains("._"))
                            keepUnderscoreTypes.Add($"{moduleName}.{name}");
                    }
                    if (keepUnderscoreTypes.Count == 0)
                        keepUnderscoreTypes = null;
                }
                var underscoreSuppressedNames = CollectUnderscoreSuppressedTypeNames(decl, keepUnderscoreTypes);

                // Synthesize underscored protocols that swift-api-digester drops from ABI JSON
                // (e.g. AppIntents._IntentValue). Inject into moduleTypes so ModuleProcessor
                // produces a TypeRecord with the correct ProtocolDescriptorSymbol, then fold
                // the synthesized names into underscoreSuppressedNames so the wrapper
                // post-processor and MemberValidationPipeline treat them as internal — the
                // synthesized decl has no members and must not surface as a C# interface.
                var synthesizedUnderscoreNames = UnderscoreProtocolSynthesizer.Synthesize(
                    moduleName, swiftInterfacePath, decl, moduleTypes, typeDatabase, logger);
                if (synthesizedUnderscoreNames.Count > 0)
                    underscoreSuppressedNames.UnionWith(synthesizedUnderscoreNames);

                // Merge underscore-suppressed names into internalTypeNames for wrapper
                // post-processing and the Pattern-2 member-reach gate, EXCLUDING synthesized
                // public-underscore protocols (e.g. AppIntents._IntentValue). See
                // UnderscoreProtocolSynthesizer.MergeSuppressedIntoInternalTypeNames for why
                // the synthesized names must not enter the internal-reach set.
                internalTypeNames = UnderscoreProtocolSynthesizer.MergeSuppressedIntoInternalTypeNames(
                    internalTypeNames, underscoreSuppressedNames, synthesizedUnderscoreNames);
                if (underscoreSuppressedNames.Count > 0)
                {
                    logger.LogInformation("Suppressing {Count} underscore-prefixed types from C# output", underscoreSuppressedNames.Count);
                }
                // Re-sync the property so emission-time gates (MemberValidationPipeline)
                // see the same final set as the wrapper post-processor — the local was
                // possibly created here when CollectInternalTypeNames returned an empty
                // set and the underscore merge had to allocate.
                decl.InternalTypeNames = internalTypeNames;
                ReportCollector.Start(decl);

                // dylibPath is used for metadata extraction, runtimeLibraryName is used in generated DllImport
                var moduleProcessor = new ModuleProcessor(moduleName, dylibPath, runtimeLibraryName, moduleTypes, typeDatabase, loggerFactory.CreateLogger<ModuleProcessor>(), namespaceResolver);
                var moduleDatabase = moduleProcessor.FinalizeTypeProcessingAndCreateModuleDatabase().ModuleDatabase;
                typeDatabase.AddModuleDatabase(moduleDatabase);

                logger.LogDebug("Parsed Swift ABI file successfully.");

                // Create per-module emission context (replaces static mutable state + ResetForModule)
                var emissionContext = new ModuleEmissionContext();
                emissionContext.SetUnderscoreSuppressedNames(underscoreSuppressedNames);
                emissionContext.SetCollisionContext(moduleNameForCollision, nestedTypesInCollidingClass);

                // Create concrete specialization engine and index module-local conformances
                var specializationEngine = new ConcreteSpecializationEngine(typeDatabase, moduleName);
                specializationEngine.IndexModuleConformances(decl);
                emissionContext.SpecializationEngine = specializationEngine;

                // Protocol names, protocol-extension methods, and foreign-type extension members
                // all come from the producer-aggregated SwiftInterfaceFacts. The aggregator
                // already routed each fact through whichever producer (regex or SwiftSyntax)
                // covers it; downstream phases consume facts.* directly so the choice of
                // producer is transparent here.
                var protocolNames = facts.ProtocolNames;

                // Inject protocol extension methods onto conforming types.
                ProtocolExtensionDefaultsIndex? extensionDefaultsIndex = null;
                if (protocolNames.Count > 0)
                {
                    var extensionMethods = facts.ProtocolExtensionMethods;
                    if (extensionMethods.Count > 0)
                    {
                        // Build extension defaults index BEFORE injection — used by validator to allow
                        // conformance when types rely on protocol extension default implementations.
                        extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(extensionMethods, decl.Protocols);
                        emissionContext.ExtensionDefaultsIndex = extensionDefaultsIndex;

                        ProtocolExtensionEmitter.InjectExtensionMethods(decl, extensionMethods, typeDatabase, logger, emissionContext);
                    }
                }

                // Detect phantom defaults — required protocol members that no conforming type
                // can implement in C#, indicating they're satisfied by PAT extension defaults
                // not visible in the public ABI. These become default interface methods in C#.
                if (decl.Protocols.Count > 0)
                {
                    extensionDefaultsIndex ??= new ProtocolExtensionDefaultsIndex(new(), decl.Protocols);
                    extensionDefaultsIndex.DetectPhantomDefaults(decl, typeDatabase);
                    emissionContext.ExtensionDefaultsIndex = extensionDefaultsIndex;
                }

                // Foreign type extension members are partitioned from facts.ExtensionMemberCandidates
                // using the parsed module's name + own type set; the partitioning lives on facts so
                // both producers feed it identical inputs.
                if (facts.ExtensionMemberCandidates.Count > 0)
                {
                    var moduleTypeNames = new HashSet<string>(decl.Types.Select(t => t.Name));
                    var foreignExtensions = facts.ResolveForeignExtensions(moduleName, moduleTypeNames);
                    if (foreignExtensions.Count > 0)
                    {
                        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
                            decl, foreignExtensions, typeDatabase, logger, emissionContext,
                            facts.AvailabilityAnnotations);
                    }
                }

                // Reset Apple supplement tracker at module boundary — stale references from a
                // previous module must not leak into this module's emitted csproj.
                AppleSupplementReferences.Reset();

                // Emit the C# bindings
                var stringEmitter = new StringEmitter(outputDirectory, typeDatabase, loggerFactory, namespaceResolver, bridgeHintsPath, facts.MarkerProtocolConformances);
                stringEmitter.EmitModule(decl, emissionContext);

                var report = ReportCollector.Complete();
                ReportCollector.Reset();

                // Co-gate method bodies that reference suppressed proxy classes.
                // When EveryProtocol conformance is skipped, the proxy class is not emitted.
                // Method bodies in other types that construct the proxy (existential return
                // unwrappers, optional property getters) must also be removed.
                //
                // Cross-module case: the umbrella-aware existential marshaler can emit
                // `{Namespace}.SwiftInterop.{ProxyName}(` references targeting a proxy that
                // lives in a previously generated dependency module. If that dependency
                // suppressed the proxy, its <suppressedProxies> XML element flows here via
                // TypeDatabase as `(namespace, proxyName)` pairs (namespace = the dep's C#
                // namespace, persisted by ModuleDatabaseEmitter). We pass them to the
                // post-pass as a separate qualified-only set so it strips ONLY the
                // cross-module qualified form, never the unqualified `new {ProxyName}(` or
                // `new SwiftInterop.{ProxyName}(` forms — those would false-positive on
                // this module's own legitimately-emitted proxy with the same simple class
                // name.
                var crossModulePairs = typeDatabase.GetCrossModuleSuppressedProxyClassNames();
                IReadOnlySet<string> crossModuleQualified;
                if (crossModulePairs.Count > 0)
                {
                    var qualified = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (ns, proxyName) in crossModulePairs)
                        qualified.Add($"{ns}.SwiftInterop.{proxyName}");
                    crossModuleQualified = qualified;
                }
                else
                {
                    crossModuleQualified = new HashSet<string>(StringComparer.Ordinal);
                }

                IReadOnlyList<CoGatedMember> proxyCoGated = Array.Empty<CoGatedMember>();
                if (emissionContext.SuppressedProxyClassNames.Count > 0 || crossModuleQualified.Count > 0)
                {
                    proxyCoGated = CSharpWrapperCoGater.ProcessSuppressedProxyReferencesInDirectory(
                        outputDirectory,
                        emissionContext.SuppressedProxyClassNames,
                        crossModuleQualified,
                        logger);
                    if (proxyCoGated.Count > 0)
                        logger.LogInformation(
                            "Suppressed {Count} method(s) referencing {LocalCount} local + {CrossCount} cross-module suppressed proxy class(es).",
                            proxyCoGated.Count,
                            emissionContext.SuppressedProxyClassNames.Count,
                            crossModuleQualified.Count);
                }

                // Strip orphan callers left behind by in-band wrapper-symbol contract
                // rejections. The contract trips inside PInvokeEmitter AFTER WrapperEmitter
                // has already written the wrapper body to disk — the P/Invoke decl is
                // suppressed but the call site referencing it remains. RecordContractViolation
                // collected those C# P/Invoke method names during emission; the cogater
                // strips every caller through the same transitive-closure logic that
                // handles wrapper-compilation strips.
                IReadOnlyList<CoGatedMember> contractCoGated = Array.Empty<CoGatedMember>();
                if (emissionContext.ContractViolatedPInvokeScopes.Count > 0)
                {
                    contractCoGated = CSharpWrapperCoGater.ProcessDirectoryForContractViolations(
                        outputDirectory,
                        emissionContext.ContractViolatedPInvokeScopes,
                        logger);
                    if (contractCoGated.Count > 0)
                        logger.LogInformation(
                            "Stripped {Count} caller(s) of {ViolationCount} contract-rejected P/Invoke(s).",
                            contractCoGated.Count,
                            emissionContext.ContractViolatedPInvokeScopes.Count);
                }

                // Emit the ILLink trimmer descriptor that roots every open-generic ISwiftObject
                // type definition emitted in this module. ILC's reachability analysis can prove
                // a call edge to the closed instantiations the eager-cctor pattern touches, but
                // it does NOT preserve reflection metadata for the open generic type definition
                // itself — so other closed instantiations resolved at runtime through
                // SwiftObjectReflectionHelper are dead-code-eliminated and fail with missing
                // metadata on device. The runtime ships an equivalent descriptor for
                // Swift.SwiftArray`1; this is the per-module counterpart. The descriptor
                // is load-bearing alongside the cctor pattern for NativeAOT trimming.
                //
                // The assembly fullname must match the produced assembly's short name. The csproj
                // emitted by BindingProjectEmitter does not set <AssemblyName> explicitly, so the
                // assembly name defaults to MSBuildProjectName = the packageId. Wildcard fullname
                // would over-match across the trimmed closed-world; resolving the exact name keeps
                // the descriptor scoped to this binding's assembly. When no platform was threaded
                // in (e.g., unit-test paths that don't ship a binding), fall back to iOS so the
                // descriptor still writes a coherent file rather than silently dropping the gate.
                var descriptorPlatform = PlatformInfoFactory.Create(platform ?? ApplePlatform.iOS);
                var descriptorAssemblyName = descriptorPlatform.GetDefaultSwiftPackageId(moduleName);
                TrimmerDescriptorEmitter.Emit(emissionContext, outputDirectory, descriptorAssemblyName, logger);

                // Emit emission-level metrics (wrapper strategies, conformance decisions)
                EmissionReportEmitter.Emit(emissionContext, moduleName, outputDirectory, logger);

                // Build and write the binding artifact manifest. The main generation pass
                // owns this output directory and replaces any prior artifact wholesale —
                // an existing binding-report.json from a pre-M1 build (no manifest) is fine
                // and gets overwritten. Wrapper/bridge phases use ReadModifyWrite, which
                // rejects orphaned reports because they own only their own section.
                if (report != null)
                {
                    var emissionReport = EmissionReportEmitter.BuildReport(emissionContext, moduleName);
                    var manifest = new BindingArtifactManifest
                    {
                        Module = moduleName,
                        GeneratorVersion = BindingArtifactManifestStore.GetGeneratorVersion(),
                        Generation = GenerationSection.From(report),
                        Emission = EmissionSection.From(emissionReport),
                        ProxyCoGating = new ProxyCoGatingSection
                        {
                            Status = PhaseStatus.Success,
                            SuppressedProxyClassCount = emissionContext.SuppressedProxyClassNames.Count,
                            CoGatedMethods = proxyCoGated.ToList(),
                        },
                        ContractCoGating = new ContractCoGatingSection
                        {
                            Status = PhaseStatus.Success,
                            ContractViolatedPInvokeCount = emissionContext.ContractViolatedPInvokeScopes.Count,
                            CoGatedMembers = contractCoGated.ToList(),
                        },
                    };
                    BindingArtifactManifestStore.Write(manifest, outputDirectory, logger);

                    var projectedReport = BindingReportProjection.Project(manifest);
                    var reportPath = Path.Combine(outputDirectory, BindingArtifactManifestStore.ReportFileName);
                    ReportEmitter.LogSummary(projectedReport, logger, reportPath);
                }

                // Fixup protocol EmittedMemberCount to include inherited requirements.
                // Must run after EmitModule (all direct counts set) and before database serialization.
                ProtocolHandler.FixupProtocolInheritedRequirements(decl, typeDatabase);

                // Stamp emitted class instance methods onto each Class TypeRecord so a
                // downstream module can verify cross-module `override` modifiers. Must run after
                // EmitModule (WasEmitted bits set) and before database serialization.
                ClassHandler.PopulateEmittedClassMethods(decl, typeDatabase);

                // Emit module database XML for cross-module resolution by downstream modules.
                // Pass the local emission's suppressed proxy class names so downstream modules
                // can strip cross-module qualified references to those suppressed proxies.
                // The namespace is the C# namespace into which the proxies would have been
                // emitted (`{generatedNamespace}.SwiftInterop` minus the trailing
                // `.SwiftInterop`). Persist it so downstream post-passes can build the exact
                // qualified-form needle — `QualifyProxyClassName` uses the protocol record's
                // C# namespace, which diverges from the Swift module name under a custom
                // `namespacePattern`.
                ModuleDatabaseEmitter.Emit(
                    moduleDatabase,
                    outputDirectory,
                    logger,
                    emissionContext.SuppressedProxyClassNames.Count > 0
                        ? (IReadOnlyCollection<string>)emissionContext.SuppressedProxyClassNames
                        : null,
                    suppressedProxyNamespace: namespaceResolver.ResolveNamespace(moduleName));

                logger.LogInformation("Bindings generation completed for {SwiftAbiPath}.", swiftAbiPath);

            }
            else
                logger.LogWarning("Bindings generation already completed for {SwiftAbiPath}.", swiftAbiPath);

            return true;
            }
            catch (Exception ex)
            {
                logger.LogError("Binding generation failed: {Message}", ex.Message);
                logger.LogDebug("Stack trace:\n{StackTrace}", ex.ToString());
                ReportCollector.Reset();
                return false;
            }
        }

        /// <summary>
        /// Returns the list of built-in database XML file names to load for a given platform.
        /// Apple-framework stubs that are absent on the target platform are filtered out so
        /// lookups don't accidentally resolve types that can't exist at runtime.
        /// </summary>
        internal static IReadOnlyList<string> GetBuiltInDatabases(ApplePlatform? platform)
        {
            var result = new List<string>
            {
                "FoundationDatabase.xml", "SwiftDatabase.xml", "_ConcurrencyDatabase.xml",
                "CoreGraphicsDatabase.xml",
                "DispatchDatabase.xml", "CoreImageDatabase.xml", "SwiftUIDatabase.xml",
                "AVFoundationDatabase.xml", "CoreTextDatabase.xml", "SecurityDatabase.xml",
                "QuartzCoreDatabase.xml", "PhotosDatabase.xml", "CoreBluetoothDatabase.xml",
                "CoreLocationDatabase.xml", "MapKitDatabase.xml", "MatterDatabase.xml", "MetalDatabase.xml",
                "CoreMLDatabase.xml", "StoreKitDatabase.xml", "SceneKitDatabase.xml",
                "NaturalLanguageDatabase.xml", "CoreMediaDatabase.xml", "ManagedSettingsDatabase.xml",
                // simd is a C module shipped by every Apple platform; the database exposes
                // simd_float4x4 as a System.Numerics.Matrix4x4 projection so consumers of
                // ARKit / RoomPlan transforms get a usable managed type.
                "SimdDatabase.xml",
            };
            // UIKit: available on all platforms except macOS (Catalyst has UIKit)
            if (platform != ApplePlatform.macOS)
                result.Add("UIKitDatabase.xml");
            // HealthKit: unavailable on macOS per apple-frameworks.json
            if (platform != ApplePlatform.macOS)
                result.Add("HealthKitDatabase.xml");
            // AppKit: macOS and Catalyst only (Catalyst has AppKit compatibility layer)
            if (platform == null || platform == ApplePlatform.macOS || platform == ApplePlatform.MacCatalyst)
                result.Add("AppKitDatabase.xml");
            return result;
        }

        /// <summary>
        /// Compile-wrapper-only mode: resolves the xcframework, compiles existing .swift wrapper files,
        /// and updates binding-metadata.props. Skips all parsing and C# generation.
        /// </summary>
        /// <summary>
        /// Parses the <c>--target-architectures</c> value (comma-separated) into a normalized,
        /// de-duplicated list with arm64 ordered first — it is the primary pass and carries the
        /// device slice (there is no Intel device), so extra arches are folded into it. Returns an
        /// empty list when unset (keep the historical per-slice arch preference) or null on an
        /// unrecognized token (error already logged). Accepts the NuGet <c>x64</c> spelling as an
        /// alias for Apple's <c>x86_64</c>.
        /// </summary>
        internal static List<string>? ParseTargetArchitectures(string? value, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new List<string>();

            var result = new List<string>();
            foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var arch = raw.ToLowerInvariant();
                if (arch == "x64") arch = "x86_64";
                if (arch != "arm64" && arch != "x86_64")
                {
                    logger.LogError("Error: Invalid --target-architectures token '{Token}'. Valid: arm64, x86_64 (x64).", raw);
                    return null;
                }
                if (!result.Contains(arch))
                    result.Add(arch);
            }

            result.Sort((a, b) => a == b ? 0 : (a == "arm64" ? -1 : (b == "arm64" ? 1 : string.CompareOrdinal(a, b))));
            return result;
        }

        /// <summary>
        /// Decides which CPU architectures the wrapper is compiled for, given the source slice's
        /// available architectures. Splits the result into a primary pass (<paramref name="primaryArch"/>,
        /// always built) and zero or more <paramref name="extraArchs"/> that are lipo-merged into the
        /// primary to form one fat wrapper xcframework.
        ///
        /// <para><b>auto</b> (<paramref name="autoMatchSource"/> true): match the source's coverage.
        /// When the source ships an arm variant, the primary pass stays <c>null</c> (historical
        /// arm64/arm64e preference, so an arm64e-only device slice is not silently dropped) and x86_64
        /// is folded in as an extra only when present. When the source is <em>x86_64-only</em> (a legacy
        /// Intel-only library), the primary pass is pinned to <c>x86_64</c> with no extras — leaving it
        /// <c>null</c> would resolve to x86_64 AND schedule a second x86_64 pass, then lipo two
        /// same-arch binaries. Never fails.</para>
        ///
        /// <para><b>explicit</b> list: every requested arch must be present in the source slice, or this
        /// returns <c>false</c> after logging SWIFTBIND052. Validating up front — before the destructive
        /// per-arch compile + lipo merge — keeps the failure loud instead of letting a mid-merge throw be
        /// downgraded to an SDK-mode warning.</para>
        /// </summary>
        internal static bool TryDecideWrapperArchitectures(
            bool autoMatchSource,
            IReadOnlyList<string> requestedArchs,
            IReadOnlyList<string> sourceArchitectures,
            string sourceSliceId,
            ILogger logger,
            out string? primaryArch,
            out List<string> extraArchs)
        {
            bool SourceHas(string arch) =>
                sourceArchitectures.Any(a => string.Equals(a, arch, StringComparison.OrdinalIgnoreCase));

            if (autoMatchSource)
            {
                var hasArm = SourceHas("arm64") || SourceHas("arm64e");
                var hasX64 = SourceHas("x86_64");
                if (hasX64 && !hasArm)
                {
                    // x86_64-only source: build the wrapper for x86_64 alone. A null primary
                    // would itself resolve to x86_64 and the extra pass would compile it again,
                    // leaving the merger to lipo two identical-arch binaries.
                    primaryArch = "x86_64";
                    extraArchs = new List<string>();
                }
                else if (hasX64)
                {
                    // arm + x86_64 fat source: fold x86_64 in as an extra, but pin the PRIMARY to a
                    // concrete arm arch. A null primary defers to SelectArchitecture, which prefers an
                    // exact "arm64" and otherwise returns the slice's FIRST arch — so an arm64e+x86_64
                    // slice that lists x86_64 first would resolve the primary to x86_64, drop arm64e,
                    // and (via the degrade-on-fold-failure path) ship an x86_64-only wrapper. Prefer
                    // arm64; otherwise the arm64e variant that is present.
                    primaryArch = SourceHas("arm64") ? "arm64" : "arm64e";
                    extraArchs = new List<string> { "x86_64" };
                }
                else
                {
                    // arm-only source (no x86_64): keep the historical null primary — SelectArchitecture
                    // resolves arm64, or arm64e on an arm64e-only slice (its only/first arch).
                    primaryArch = null;
                    extraArchs = new List<string>();
                }
                logger.LogInformation(
                    "--target-architectures auto: source slice '{Slice}' provides [{Available}]; building wrapper for [{Wrapper}].",
                    sourceSliceId,
                    string.Join("+", sourceArchitectures),
                    string.Join("+", new[] { primaryArch ?? "arm64" }.Concat(extraArchs)));
                return true;
            }

            foreach (var arch in requestedArchs)
            {
                if (!SourceHas(arch))
                {
                    primaryArch = null;
                    extraArchs = new List<string>();
                    logger.LogError(
                        "SWIFTBIND052: --target-architectures requested '{Arch}', but source slice '{Slice}' ships only " +
                        "[{Available}]. Refusing to fall back to a narrower wrapper — the source library must provide a " +
                        "'{Arch}' slice for this platform.",
                        arch, sourceSliceId, string.Join("+", sourceArchitectures), arch);
                    return false;
                }
            }

            primaryArch = requestedArchs.Count > 0 ? requestedArchs[0] : null;
            extraArchs = requestedArchs.Count > 1 ? requestedArchs.Skip(1).ToList() : new List<string>();
            return true;
        }

        /// <summary>
        /// Picks the source architectures the <c>auto</c> fat-or-not decision should be based on.
        /// x86_64 only ever ships in the simulator/host slice (there is no Intel device), so when
        /// metadata resolution was pinned to the device slice (<c>--platform-target device</c>) but the
        /// wrapper still covers the simulator family (<c>all</c>/<c>simulator</c>), the device slice's
        /// arm-only arch list would wrongly suppress x86_64. Re-resolve the simulator slice in that case;
        /// fall back to the already-resolved slice when there is no simulator slice (device-only coverage,
        /// where arm-only is correct).
        /// </summary>
        internal static (IReadOnlyList<string> Architectures, string SliceId) ResolveAutoArchBasis(
            XCFrameworkResolution resolution,
            string xcframeworkPath, string outputDirectory,
            XCFrameworkPlatformTarget platformTarget, string wrapperArchNormalized,
            PlatformInfo platformInfo, ILogger logger,
            IReadOnlyList<string>? companionFrameworkPaths = null)
        {
            if (platformTarget == XCFrameworkPlatformTarget.Device && wrapperArchNormalized != "device")
            {
                try
                {
                    var sim = XCFrameworkResolver.Resolve(
                        xcframeworkPath, outputDirectory,
                        XCFrameworkPlatformTarget.Simulator, logger, platformInfo: platformInfo,
                        companionFrameworkPaths: companionFrameworkPaths);
                    return (sim.SupportedArchitectures, sim.LibraryIdentifier);
                }
                catch
                {
                    // No simulator slice — the wrapper covers device only, so the device slice's
                    // (arm-only) arch list is the correct basis.
                }
            }
            return (resolution.SupportedArchitectures, resolution.LibraryIdentifier);
        }

        /// <summary>
        /// Synthetic <c>auto</c> arch basis for Apple-framework direct mode, which has NO source
        /// xcframework to inspect. Apple system frameworks always ship fat where they ship Intel at
        /// all: iOS/tvOS simulator, macOS, and Mac Catalyst slices are arm64+x86_64; iOS/tvOS device
        /// slices are arm64 only (no Intel device target exists).
        ///
        /// Basis MUST reflect what the wrapper xcframework can ship, NOT what the active compile
        /// slice happens to have. The SDK (<c>_AFW_OtherIsFatSim</c> in Sdk.targets) packs the sim
        /// slice as the second wrapper slice even when <c>SwiftPlatformTarget=device</c>, so the
        /// wrapper coverage spans both slices on iOS/tvOS. Feeding the active <c>DeviceSlice</c>
        /// here would return arm-only and cause <see cref="TryDecideWrapperArchitectures"/> to
        /// reject explicit <c>arm64,x86_64</c> with SWIFTBIND052 — even though the SDK would
        /// happily build the fat sim second slice. Mirror the xcframework path's
        /// <see cref="ResolveAutoArchBasis"/> "re-resolve sim" pattern: prefer the simulator slice
        /// when present (its natural arch list IS the wrapper's max arch coverage); fall back to
        /// the device slice for macOS / MacCatalyst (no sim variant, single slice already ships
        /// fat). This keeps <c>auto</c> and explicit <c>arm64,x86_64</c> producing the same fat
        /// wrapper xcframework for a StoreKit / WeatherKit / etc. binding in either sim-first or
        /// device-first SDK configuration.
        /// </summary>
        internal static (IReadOnlyList<string> Architectures, string SliceId) ResolveAppleFrameworkAutoArchBasis(
            PlatformInfo platformInfo)
        {
            var basisSlice = platformInfo.SimulatorSlice ?? platformInfo.DeviceSlice;
            return (GetAppleFrameworkSliceNaturalArchs(basisSlice), basisSlice.SliceId);
        }

        /// <summary>
        /// Arches that can natively be compiled against this single Apple-framework slice. iOS/tvOS
        /// simulator + macOS + MacCatalyst slices ship arm64+x86_64; iOS/tvOS device slices ship
        /// arm64 only (no Intel device target exists). Used to filter wrapper extra-arch compiles
        /// down to what the active slice can actually produce — the M1 contract has the wrapper
        /// coverage span both device + sim, but the GENERATOR only compiles the active platform
        /// target's slice. Extra arches that don't fit (e.g. x86_64 against an iOS/tvOS device
        /// slice) are not lost: the SDK's <c>_AFW_OtherIsFatSim</c> path fat-folds them into the
        /// second wrapper slice. Letting swiftc try anyway leaves a malformed slice dir that
        /// breaks the SDK's downstream <c>xcodebuild -create-xcframework</c> merge.
        /// </summary>
        internal static IReadOnlyList<string> GetAppleFrameworkSliceNaturalArchs(SliceVariant slice) =>
            slice.IsSimulator || !RequiresArmOnlyDevice(slice.Platform)
                ? new[] { "arm64", "x86_64" }
                : new[] { "arm64" };

        private static bool RequiresArmOnlyDevice(ApplePlatform platform) =>
            platform == ApplePlatform.iOS || platform == ApplePlatform.tvOS;

        /// <summary>
        /// Compiles the wrapper for <paramref name="primaryArch"/>, then lipo-folds each
        /// <paramref name="extraArchs"/> wrapper xcframework into it to form one fat build. Shared by the
        /// standalone generation path and <c>--compile-wrapper-only</c> so both honor
        /// <c>--target-architectures</c> identically. The primary pass carries the device slice (no Intel
        /// counterpart); the merger keeps such single-arch slices as-is. Returns the primary result, whose
        /// <c>XCFrameworkPath</c> now points at the merged fat build.
        /// </summary>
        internal static SwiftWrapperCompilationResult? CompileWrapperForArchitectures(
            string? primaryArch,
            IReadOnlyList<string> extraArchs,
            Func<string?, SwiftWrapperCompilationResult?> compileForArch,
            ILogger logger)
        {
            var compilationResult = compileForArch(primaryArch);

            var wrapperXcfwPath = compilationResult?.XCFrameworkPath;
            if (extraArchs.Count > 0 && !string.IsNullOrEmpty(wrapperXcfwPath) && Directory.Exists(wrapperXcfwPath))
            {
                var primaryAside = wrapperXcfwPath + ".primary";
                if (Directory.Exists(primaryAside)) Directory.Delete(primaryAside, true);
                Directory.Move(wrapperXcfwPath, primaryAside);

                try
                {
                    foreach (var arch in extraArchs)
                    {
                        var extraResult = compileForArch(arch);
                        var extraPath = extraResult?.XCFrameworkPath;
                        if (string.IsNullOrEmpty(extraPath) || !Directory.Exists(extraPath))
                        {
                            logger.LogWarning("Target arch '{Arch}' produced no wrapper xcframework; skipping its merge.", arch);
                            continue;
                        }
                        var secondaryAside = wrapperXcfwPath + "." + arch;
                        if (Directory.Exists(secondaryAside)) Directory.Delete(secondaryAside, true);
                        Directory.Move(extraPath, secondaryAside);
                        WrapperXCFrameworkMerger.MergeFatSlices(primaryAside, secondaryAside, logger);
                    }

                    logger.LogInformation("Merged wrapper xcframework into a fat build ({Archs}).",
                        string.Join(" + ", new[] { primaryArch ?? "arm64" }.Concat(extraArchs)));
                }
                catch (Exception ex)
                {
                    // Degrade to the primary-only wrapper rather than propagating. The extra-arch fold
                    // is best-effort: a failed x86_64 compile/lipo must NOT take down the working
                    // primary. We deliberately SWALLOW (not rethrow) so the returned compilationResult
                    // stays non-null — the SDK --compile-wrapper-only path catches a propagated
                    // exception, leaves compilationResult null, and then records
                    // _SwiftBindingHasWrapperXCFramework=False off that null even though the primary is
                    // restored on disk below, dropping the NativeReference for EVERY consumer
                    // (arm64 included). Returning the primary result keeps metadata truthful: the
                    // package ships the primary-arch wrapper, just not the fat fold. This mirrors the
                    // SDK's existing sdkMode contract, where a primary wrapper failure is also a warning.
                    logger.LogWarning(ex,
                        "Folding extra architecture(s) [{Archs}] into the wrapper failed; shipping the "
                        + "{Primary}-only wrapper instead. Consumers targeting the dropped arch(es) will "
                        + "not resolve a matching native slice.",
                        string.Join(", ", extraArchs), primaryArch ?? "arm64");
                }
                finally
                {
                    // Always move the primary back into place. MergeFatSlices is transactional — it
                    // builds the fat result in a staging copy and only swaps it in after the whole
                    // merge succeeds — so a mid-merge throw (a failed extra compile or lipo) leaves the
                    // primary tree byte-for-byte intact, and the restored primary still carries a valid
                    // primary-arch wrapper with a matching single-arch plist.
                    if (Directory.Exists(primaryAside)) Directory.Move(primaryAside, wrapperXcfwPath);
                }
            }

            return compilationResult;
        }

        internal static int RunCompileWrapperOnly(
            string xcframeworkPath, string outputDirectory,
            string? platformStr, string? platformTargetStr,
            string? wrapperArchitectures, string[]? frameworkDependencies,
            ILogger logger, PlatformInfo platformInfo,
            bool skipThunkCompilation = false,
            string? targetArchitectures = null,
            IReadOnlyList<string>? linkFrameworks = null,
            IReadOnlyList<string>? linkLibraries = null)
        {
            var wrapperArchNormalized = wrapperArchitectures?.ToLowerInvariant() ?? "simulator";
            if (wrapperArchNormalized != "simulator" && wrapperArchNormalized != "device" && wrapperArchNormalized != "all")
            {
                logger.LogError("Error: Invalid --wrapper-architectures '{Value}'. Valid values: 'simulator', 'device', 'all'.", wrapperArchitectures);
                return 1;
            }

            // CPU target arch(es) — distinct from the slice TYPE in wrapperArchitectures.
            //   "auto"      => match the source slice's arch coverage: a fat wrapper iff the
            //                  source is fat, arm64-only otherwise. The SDK passes this so a
            //                  single runtimes/<rid>/native/ tree serves both Apple Silicon and
            //                  Intel/Rosetta without breaking the very common arm64-only source.
            //   explicit X  => compile exactly those and fail loud (SWIFTBIND052) if the source
            //                  lacks one — never silently narrow the wrapper.
            //   empty/unset => historical single-pass arm64-preference (no merge).
            // Decided into primaryArch + extraArchs once the source slice is resolved, below.
            var autoMatchSource = string.Equals(targetArchitectures?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
            List<string> requestedArchs;
            if (autoMatchSource)
            {
                requestedArchs = new List<string>(); // resolved from the source slice below
            }
            else
            {
                var parsed = ParseTargetArchitectures(targetArchitectures, logger);
                if (parsed == null)
                    return 1; // invalid arch token already logged
                requestedArchs = parsed;
            }

            var platformTarget = XCFrameworkPlatformTarget.Simulator;
            switch (platformTargetStr?.ToLowerInvariant())
            {
                case "simulator":
                case null:
                    platformTarget = XCFrameworkPlatformTarget.Simulator;
                    break;
                case "device":
                    platformTarget = XCFrameworkPlatformTarget.Device;
                    break;
                default:
                    logger.LogError("Error: Invalid --platform-target '{Value}'.", platformTargetStr);
                    return 1;
            }

            // Resolve xcframework to get module name and search paths
            XCFrameworkResolution resolution;
            try
            {
                resolution = XCFrameworkResolver.Resolve(
                    xcframeworkPath, outputDirectory, platformTarget, logger, platformInfo: platformInfo,
                    companionFrameworkPaths: frameworkDependencies);
            }
            catch (Exception ex)
            {
                logger.LogError("Error resolving xcframework: {Message}", ex.Message);
                return 1;
            }

            var moduleName = resolution.ModuleName;

            // Decide the wrapper's CPU arch passes from the SOURCE slice. x86_64 lives only in the
            // simulator/macOS slice (there is no Intel device); ResolveAutoArchBasis re-resolves the
            // simulator slice when --platform-target pinned the resolution to the device slice, so a
            // fat sim slice is still detected for both the auto fold and explicit-arch validation.
            var (autoBasisArchs, autoBasisSliceId) = ResolveAutoArchBasis(
                resolution, xcframeworkPath, outputDirectory, platformTarget, wrapperArchNormalized,
                platformInfo, logger, companionFrameworkPaths: frameworkDependencies);
            if (!TryDecideWrapperArchitectures(
                    autoMatchSource, requestedArchs, autoBasisArchs,
                    autoBasisSliceId, logger, out var primaryArch, out var extraArchs))
            {
                return 1; // explicit arch missing from source — already logged (SWIFTBIND052)
            }

            // Resolve framework dependency search paths
            List<FrameworkDependencyInfo>? resolvedDeps = null;
            if (frameworkDependencies != null && frameworkDependencies.Length > 0)
            {
                resolvedDeps = ResolveFrameworkDependencies(
                    frameworkDependencies, resolution, xcframeworkPath,
                    wrapperArchNormalized, platformTarget, logger, platformInfo: platformInfo);
                if (resolvedDeps == null)
                    return 1;
            }

            var simDepPaths = resolvedDeps?
                .Where(d => d.SimulatorFrameworkSearchPath != null)
                .Select(d => d.SimulatorFrameworkSearchPath!)
                .ToList();
            var deviceDepPaths = resolvedDeps?
                .Where(d => d.DeviceFrameworkSearchPath != null)
                .Select(d => d.DeviceFrameworkSearchPath!)
                .ToList();

            // Gap (a): mirror ABI extraction's co-located sibling auto-detection on the wrapper
            // compile, so a companion xcframework dropped next to the source resolves its module
            // for swiftc just as it already does for ABI generation. Explicit --framework-dependency
            // paths keep priority; siblings are merged in.
            simDepPaths = XCFrameworkResolver.MergeWrapperDependencySearchPaths(
                simDepPaths, xcframeworkPath, XCFrameworkPlatformTarget.Simulator, logger, platformInfo);
            deviceDepPaths = XCFrameworkResolver.MergeWrapperDependencySearchPaths(
                deviceDepPaths, xcframeworkPath, XCFrameworkPlatformTarget.Device, logger, platformInfo);

            // Load wrapper compilation context saved by the generation pass
            var (internalTypeNames, moduleNameForCollision, nestedTypesInCollidingClass, depModuleCollisions) =
                LoadWrapperContext(outputDirectory, logger);

            // The generation pass (--skip-wrapper-compilation) is invoked without
            // --framework-dependency, so DepModuleCollisionDetector cannot run there and
            // wrapper-context.json carries empty lists. Re-run detection here using the
            // resolvedDeps available in wrapper-only mode so dep-module/type collisions
            // (e.g., GTMSessionFetcher class colliding with module GTMSessionFetcher) are
            // patched before swiftc imports them. A reused output directory may carry a
            // stale wrapper-context.json from a prior generation with different deps; drop
            // any stored entries that don't match a currently resolved dep module.
            if (resolvedDeps != null && resolvedDeps.Count > 0)
            {
                var resolvedDepModuleNames = new HashSet<string>(
                    resolvedDeps.Select(d => d.ModuleName));
                var simList = depModuleCollisions.Simulator
                    .Where(n => resolvedDepModuleNames.Contains(n))
                    .ToList();
                var deviceList = depModuleCollisions.Device
                    .Where(n => resolvedDepModuleNames.Contains(n))
                    .ToList();
                var detected = DepModuleCollisionDetector.DetectPerSlice(resolvedDeps, platformInfo, logger);
                if (detected.Simulator.Count > 0)
                {
                    var merged = new HashSet<string>(simList);
                    merged.UnionWith(detected.Simulator);
                    simList = merged.ToList();
                }
                if (detected.Device.Count > 0)
                {
                    var merged = new HashSet<string>(deviceList);
                    merged.UnionWith(detected.Device);
                    deviceList = merged.ToList();
                }
                depModuleCollisions = new DepModuleCollisionDetector.SlicedCollisionResult(simList, deviceList);
            }
            else
            {
                // No resolved deps in this invocation — clear any stored list rather than
                // pass through obsolete patch targets from an earlier generation.
                depModuleCollisions = new DepModuleCollisionDetector.SlicedCollisionResult(
                    Array.Empty<string>(), Array.Empty<string>());
            }

            // Compiles the wrapper for ONE requested CPU arch (null = historical preference).
            // Re-resolves per arch so the right per-arch .swiftinterface/abi is used; the merged
            // source slice (e.g. fat macos-arm64_x86_64) shares one framework search path, so
            // re-resolution is cheap and arch-correct. Always writes the wrapper xcframework to
            // its fixed path inside outputDirectory.
            SwiftWrapperCompilationResult? CompileForArch(string? requestedArch)
            {
                if (wrapperArchNormalized == "all")
                {
                    var (simResolution, deviceResolution) = XCFrameworkResolver.ResolveAll(
                        xcframeworkPath, outputDirectory, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch,
                        companionFrameworkPaths: frameworkDependencies);

                    return SwiftWrapperCompiler.CompileAll(
                        outputDirectory, moduleName,
                        simResolution, deviceResolution, logger,
                        internalTypeNames: internalTypeNames,
                        simAdditionalSearchPaths: simDepPaths,
                        deviceAdditionalSearchPaths: deviceDepPaths,
                        skipThunkCompilation: skipThunkCompilation,
                        platformInfo: platformInfo,
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                        swiftInterfacePath: simResolution.SwiftInterfacePath,
                        depModuleNamesForCollisionSimulator: depModuleCollisions.Simulator,
                        depModuleNamesForCollisionDevice: depModuleCollisions.Device,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries);
                }
                else if (wrapperArchNormalized == "device")
                {
                    var deviceResolution = XCFrameworkResolver.Resolve(
                        xcframeworkPath, outputDirectory,
                        XCFrameworkPlatformTarget.Device, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch,
                        companionFrameworkPaths: frameworkDependencies);

                    return SwiftWrapperCompiler.CompileSlice(
                        outputDirectory, moduleName,
                        deviceResolution.FrameworkSearchPath,
                        deviceResolution.DylibPath,
                        "device", "iphoneos", logger,
                        internalTypeNames: internalTypeNames,
                        additionalFrameworkSearchPaths: deviceDepPaths,
                        platformInfo: platformInfo,
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                        swiftInterfacePath: deviceResolution.SwiftInterfacePath,
                        skipThunkCompilation: skipThunkCompilation,
                        resolvedArchitecture: deviceResolution.SelectedArchitecture,
                        depModuleNamesForCollision: depModuleCollisions.Device,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries);
                }
                else
                {
                    var simResolution = XCFrameworkResolver.Resolve(
                        xcframeworkPath, outputDirectory,
                        platformTarget, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch,
                        companionFrameworkPaths: frameworkDependencies);

                    return SwiftWrapperCompiler.Compile(
                        outputDirectory, moduleName,
                        simResolution.FrameworkSearchPath, simResolution.DylibPath, logger,
                        internalTypeNames: internalTypeNames,
                        additionalFrameworkSearchPaths: simDepPaths,
                        platformInfo: platformInfo,
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                        swiftInterfacePath: simResolution.SwiftInterfacePath,
                        skipThunkCompilation: skipThunkCompilation,
                        resolvedArchitecture: simResolution.SelectedArchitecture,
                        depModuleNamesForCollision: depModuleCollisions.Simulator,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries);
                }
            }

            // Compile the wrapper using existing .swift files in the output directory.
            SwiftWrapperCompilationResult? compilationResult = null;
            Exception? compilationException = null;

            try
            {
                compilationResult = CompileWrapperForArchitectures(primaryArch, extraArchs, CompileForArch, logger);
            }
            catch (Exception ex)
            {
                compilationException = ex;
            }

            // In compile-wrapper-only mode, always use SDK-mode outcome handling
            // (downgrade fatal to warning) since this target runs within SDK builds.
            var outcome = WrapperBuildOutcome.From(
                compilationResult, asyncLibraryAutoWired: false, sdkMode: true, compilationException);
            outcome.LogTo(logger);

            IReadOnlyList<CoGatedMember> coGated = Array.Empty<CoGatedMember>();
            if (outcome.StrippedSymbols.Count > 0)
            {
                coGated = CSharpWrapperCoGater.ProcessDirectory(
                    outputDirectory, outcome.StrippedSymbols, logger);
                if (coGated.Count > 0)
                    logger.LogInformation("Suppressed {Count} C# member(s) targeting stripped wrapper symbols.", coGated.Count);
            }

            // Record the wrapper phase in the binding artifact manifest. Standalone-CLI
            // invocations land in ReadModifyWrite's missing-manifest path and produce a
            // Partial manifest (no Generation section).
            BindingArtifactManifestStore.ReadModifyWrite(
                outputDirectory,
                moduleName,
                m => m.Wrapper = WrapperSection.From(outcome, coGated),
                logger,
                partialReasonWhenNew: "Wrapper compile invoked standalone (compile-wrapper-only); generation phase did not run in this output directory.");

            // Update binding-metadata.props with wrapper compilation result
            var hasWrapperXcfw = compilationResult?.XCFrameworkPath != null
                && Directory.Exists(compilationResult.XCFrameworkPath);
            var wrapperModuleName = $"{moduleName}SwiftBindings";

            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                outputDirectory, hasWrapperXcfw, wrapperModuleName,
                compilationResult?.SliceCount ?? 0, logger);

            return outcome.ExitCode;
        }

        /// <summary>
        /// Compile-bridge-only mode: resolves xcframework, collects *.SwiftUIBridge.swift files,
        /// compiles to {Module}Bridge.xcframework, and updates binding-metadata.props.
        /// </summary>
        internal static int RunCompileBridgeOnly(
            string xcframeworkPath, string outputDirectory,
            string? platformStr, string? platformTargetStr,
            string? wrapperArchitectures, string[]? frameworkDependencies,
            ILogger logger, PlatformInfo platformInfo,
            string? targetArchitectures = null)
        {
            var wrapperArchNormalized = wrapperArchitectures?.ToLowerInvariant() ?? "simulator";
            if (wrapperArchNormalized != "simulator" && wrapperArchNormalized != "device" && wrapperArchNormalized != "all")
            {
                logger.LogError("Error: Invalid --wrapper-architectures '{Value}'. Valid values: 'simulator', 'device', 'all'.", wrapperArchitectures);
                return 1;
            }

            // CPU target arch(es) for the bridge — mirrors the wrapper's --target-architectures so the
            // {Module}Bridge.xcframework is fat (arm64 + x86_64) exactly when the wrapper is. A bridge
            // shipped arm64-only re-introduces the same Rosetta/x64-sim DllNotFound the wrapper fat-fold
            // fixes, because the SwiftUI bridge views P/Invoke into their own native slice. Decided into
            // primaryArch + extraArchs once the source slice is resolved, below.
            var autoMatchSource = string.Equals(targetArchitectures?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
            List<string> requestedArchs;
            if (autoMatchSource)
            {
                requestedArchs = new List<string>(); // resolved from the source slice below
            }
            else
            {
                var parsed = ParseTargetArchitectures(targetArchitectures, logger);
                if (parsed == null)
                    return 1; // invalid arch token already logged
                requestedArchs = parsed;
            }

            var platformTarget = XCFrameworkPlatformTarget.Simulator;
            switch (platformTargetStr?.ToLowerInvariant())
            {
                case "simulator":
                case null:
                    platformTarget = XCFrameworkPlatformTarget.Simulator;
                    break;
                case "device":
                    platformTarget = XCFrameworkPlatformTarget.Device;
                    break;
                default:
                    logger.LogError("Error: Invalid --platform-target '{Value}'.", platformTargetStr);
                    return 1;
            }

            // Resolve xcframework to get module name and search paths
            XCFrameworkResolution resolution;
            try
            {
                resolution = XCFrameworkResolver.Resolve(
                    xcframeworkPath, outputDirectory, platformTarget, logger, platformInfo: platformInfo,
                    companionFrameworkPaths: frameworkDependencies);
            }
            catch (Exception ex)
            {
                logger.LogError("Error resolving xcframework: {Message}", ex.Message);
                return 1;
            }

            var moduleName = resolution.ModuleName;

            // Decide the bridge's CPU arch passes from the SOURCE slice (same basis as the wrapper):
            // x86_64 lives only in the simulator/macOS slice, so ResolveAutoArchBasis re-resolves the
            // simulator slice even when --platform-target pinned the resolution to the device slice.
            var (autoBasisArchs, autoBasisSliceId) = ResolveAutoArchBasis(
                resolution, xcframeworkPath, outputDirectory, platformTarget, wrapperArchNormalized,
                platformInfo, logger);
            if (!TryDecideWrapperArchitectures(
                    autoMatchSource, requestedArchs, autoBasisArchs,
                    autoBasisSliceId, logger, out var primaryArch, out var extraArchs))
            {
                return 1; // explicit arch missing from source — already logged (SWIFTBIND052)
            }

            // Check for bridge files first
            var bridgeFiles = SwiftWrapperCompiler.CollectBridgeSwiftFiles(outputDirectory);
            if (bridgeFiles.Count == 0)
            {
                logger.LogInformation("No SwiftUI bridge files found — skipping bridge compilation.");
                BindingArtifactManifestStore.ReadModifyWrite(
                    outputDirectory,
                    moduleName,
                    m => m.Bridge = new BridgeSection
                    {
                        Status = PhaseStatus.NoOp,
                        BridgeCompiled = false,
                        Message = "No SwiftUI bridge files found — bridge compilation skipped.",
                    },
                    logger,
                    partialReasonWhenNew: "Bridge compile invoked standalone (compile-bridge-only); generation phase did not run in this output directory.");
                return 0;
            }

            // Resolve framework dependency search paths.
            // Unlike wrapper compilation, bridge tolerates resolution failures — some
            // passed dependencies may be wrapper xcframeworks (from GetSwiftFrameworkSearchPaths)
            // that lack .swiftmodule and can't be fully resolved. Skip those gracefully.
            List<FrameworkDependencyInfo>? resolvedDeps = null;
            if (frameworkDependencies != null && frameworkDependencies.Length > 0)
            {
                // Filter to only xcframeworks that can be resolved (skip wrapper xcframeworks)
                var resolvableDeps = new List<string>();
                foreach (var depPath in frameworkDependencies)
                {
                    if (!Directory.Exists(depPath))
                    {
                        logger.LogDebug("Skipping non-existent dependency: {Path}", depPath);
                        continue;
                    }
                    // Check if this xcframework has an Info.plist with library entries
                    // (wrapper xcframeworks have Info.plist but no .swiftmodule)
                    var infoPlist = Path.Combine(depPath, "Info.plist");
                    if (!File.Exists(infoPlist))
                    {
                        logger.LogDebug("Skipping dependency without Info.plist: {Path}", depPath);
                        continue;
                    }
                    // Try to detect if this is a source framework (has .swiftmodule) vs wrapper
                    var hasSwiftModule = Directory.GetDirectories(depPath, "*.framework", SearchOption.AllDirectories)
                        .Any(fw => Directory.Exists(Path.Combine(fw, "Modules")));
                    if (!hasSwiftModule)
                    {
                        logger.LogDebug("Skipping wrapper/non-Swift dependency (no Modules dir): {Path}", depPath);
                        continue;
                    }
                    resolvableDeps.Add(depPath);
                }

                if (resolvableDeps.Count > 0)
                {
                    resolvedDeps = ResolveFrameworkDependencies(
                        resolvableDeps.ToArray(), resolution, xcframeworkPath,
                        wrapperArchNormalized, platformTarget, logger, platformInfo: platformInfo);
                    // Don't fail on resolution errors — bridge compilation is best-effort
                    if (resolvedDeps == null)
                        resolvedDeps = new List<FrameworkDependencyInfo>();
                }
            }

            var simDepPaths = resolvedDeps?
                .Where(d => d.SimulatorFrameworkSearchPath != null)
                .Select(d => d.SimulatorFrameworkSearchPath!)
                .ToList();
            var deviceDepPaths = resolvedDeps?
                .Where(d => d.DeviceFrameworkSearchPath != null)
                .Select(d => d.DeviceFrameworkSearchPath!)
                .ToList();

            // Compiles the bridge for ONE requested CPU arch (null = historical preference). Re-resolves
            // per arch so each pass uses the matching slice; CompileWrapperForArchitectures then lipo-folds
            // the extra-arch builds into one fat {Module}Bridge.xcframework — the same fan-out the wrapper
            // uses in RunCompileWrapperOnly's CompileForArch.
            SwiftWrapperCompilationResult? CompileBridgeForArch(string? requestedArch)
            {
                if (wrapperArchNormalized == "all")
                {
                    var (simResolution, deviceResolution) = XCFrameworkResolver.ResolveAll(
                        xcframeworkPath, outputDirectory, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch);

                    return SwiftWrapperCompiler.CompileBridgeAll(
                        outputDirectory, moduleName,
                        simResolution, deviceResolution, logger,
                        simAdditionalSearchPaths: simDepPaths,
                        deviceAdditionalSearchPaths: deviceDepPaths,
                        platformInfo: platformInfo);
                }
                else if (wrapperArchNormalized == "device")
                {
                    // Let a device-resolution failure propagate to the outer catch (bridge failure is
                    // non-fatal; the outcome below logs it). Device slices are arm64-only, so this pass
                    // never carries an x86_64 extra-arch fold.
                    var deviceResolution = XCFrameworkResolver.Resolve(
                        xcframeworkPath, outputDirectory,
                        XCFrameworkPlatformTarget.Device, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch);

                    return SwiftWrapperCompiler.CompileBridgeSlice(
                        outputDirectory, moduleName,
                        deviceResolution.FrameworkSearchPath,
                        deviceResolution.DylibPath,
                        platformInfo.DeviceSlice.WithArchitecture(deviceResolution.SelectedArchitecture),
                        logger, additionalFrameworkSearchPaths: deviceDepPaths);
                }
                else
                {
                    var simResolution = XCFrameworkResolver.Resolve(
                        xcframeworkPath, outputDirectory,
                        platformTarget, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch);

                    return SwiftWrapperCompiler.CompileBridge(
                        outputDirectory, moduleName,
                        simResolution.FrameworkSearchPath, simResolution.DylibPath, logger,
                        additionalFrameworkSearchPaths: simDepPaths,
                        platformInfo: platformInfo,
                        resolvedArchitecture: simResolution.SelectedArchitecture);
                }
            }

            // Compile bridge
            SwiftWrapperCompilationResult? compilationResult = null;
            Exception? compilationException = null;
            try
            {
                compilationResult = CompileWrapperForArchitectures(primaryArch, extraArchs, CompileBridgeForArch, logger);
            }
            catch (Exception ex)
            {
                compilationException = ex;
            }

            // Bridge compilation failure is non-fatal — C# bindings still load,
            // bridge views throw DllNotFoundException at runtime.
            var outcome = BridgeBuildOutcome.From(compilationResult, compilationException);
            outcome.LogTo(logger);

            BindingArtifactManifestStore.ReadModifyWrite(
                outputDirectory,
                moduleName,
                m => m.Bridge = BridgeSection.From(outcome),
                logger,
                partialReasonWhenNew: "Bridge compile invoked standalone (compile-bridge-only); generation phase did not run in this output directory.");

            // Update binding-metadata.props with bridge compilation result
            var bridgeModuleName = $"{moduleName}Bridge";

            XCFrameworkMetadataExtractor.UpdateMetadataPropsBridgeStatus(
                outputDirectory, outcome.BridgeCompiled, bridgeModuleName,
                compilationResult?.SliceCount ?? 0, logger);

            return 0; // Always succeed — bridge failure is non-fatal
        }

        /// <summary>
        /// Determines whether wrapper compilation should proceed based on the resolved
        /// slice type and the requested wrapper architecture scope.
        /// </summary>
        /// <param name="isSimulatorSlice">True when the primary resolution is a simulator slice.</param>
        /// <param name="wrapperArchitectures">Normalized value of --wrapper-architectures (simulator/device/all).</param>
        internal static bool ShouldCompileWrapper(bool isSimulatorSlice, string wrapperArchitectures, PlatformInfo? platformInfo = null)
        {
            // Platforms without simulator variants (macOS, Mac Catalyst) should always compile
            // with device slice when wrapperArchitectures is "simulator" (the default).
            if (platformInfo != null && !platformInfo.HasSimulatorVariant)
                return true;

            return isSimulatorSlice
                || wrapperArchitectures == "device"
                || wrapperArchitectures == "all";
        }

        private const string WrapperContextFileName = "wrapper-context.json";

        /// <summary>
        /// Persists wrapper compilation context (computed during generation) to a JSON file
        /// so that --compile-wrapper-only can read it back for the deferred compilation pass.
        /// </summary>
        internal static void SaveWrapperContext(
            string outputDirectory,
            HashSet<string>? internalTypeNames,
            string? moduleNameForCollision,
            HashSet<string>? nestedTypesInCollidingClass,
            DepModuleCollisionDetector.SlicedCollisionResult depModuleCollisions,
            ILogger logger)
        {
            var contextPath = Path.Combine(outputDirectory, WrapperContextFileName);
            var context = new JObject
            {
                ["internalTypeNames"] = internalTypeNames != null
                    ? new JArray(internalTypeNames.OrderBy(n => n).ToArray())
                    : new JArray(),
                ["moduleNameForCollision"] = moduleNameForCollision,
                ["nestedTypesInCollidingClass"] = nestedTypesInCollidingClass != null
                    ? new JArray(nestedTypesInCollidingClass.OrderBy(n => n).ToArray())
                    : new JArray(),
                // Dep-module collisions detected during generation, scoped per slice. The
                // simulator and device wrapper compiles each consume their own list so a
                // slice-asymmetric collision (e.g., device-only ObjC class shadowing the
                // dep module name) doesn't trigger qualifier stripping on the slice that
                // didn't actually expose the collision.
                ["depModuleNamesForCollisionSimulator"] =
                    new JArray(depModuleCollisions.Simulator.OrderBy(n => n).ToArray()),
                ["depModuleNamesForCollisionDevice"] =
                    new JArray(depModuleCollisions.Device.OrderBy(n => n).ToArray()),
            };
            File.WriteAllText(contextPath, context.ToString(Newtonsoft.Json.Formatting.Indented));
            logger.LogInformation("Saved wrapper context to {Path}", contextPath);
        }

        /// <summary>
        /// Loads wrapper compilation context saved by a prior generation pass.
        /// Returns null values if the context file doesn't exist (backward compatible).
        /// Legacy <c>depModuleNamesForCollision</c> single-list shape is hydrated into
        /// both simulator and device lists so old cached contexts still patch.
        /// </summary>
        internal static (HashSet<string>? internalTypeNames, string? moduleNameForCollision, HashSet<string>? nestedTypesInCollidingClass, DepModuleCollisionDetector.SlicedCollisionResult depModuleCollisions)
            LoadWrapperContext(string outputDirectory, ILogger logger)
        {
            var contextPath = Path.Combine(outputDirectory, WrapperContextFileName);
            var emptyCollisions = new DepModuleCollisionDetector.SlicedCollisionResult(
                Array.Empty<string>(), Array.Empty<string>());
            if (!File.Exists(contextPath))
            {
                logger.LogInformation("No wrapper context file at {Path} — using defaults.", contextPath);
                return (null, null, null, emptyCollisions);
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(contextPath));
                var internalTypeNames = json["internalTypeNames"]?.Values<string>()
                    .Where(n => n != null).Select(n => n!).ToHashSet();
                var moduleNameForCollision = json["moduleNameForCollision"]?.Value<string>();
                var nestedTypes = json["nestedTypesInCollidingClass"]?.Values<string>()
                    .Where(n => n != null).Select(n => n!).ToHashSet();

                var simList = json["depModuleNamesForCollisionSimulator"]?.Values<string>()
                    .Where(n => n != null).Select(n => n!).ToList();
                var deviceList = json["depModuleNamesForCollisionDevice"]?.Values<string>()
                    .Where(n => n != null).Select(n => n!).ToList();
                if (simList == null && deviceList == null)
                {
                    // Legacy: single-list shape. Apply to both slices (matches prior
                    // behavior, which was already over-patching one of the two slices).
                    var legacy = json["depModuleNamesForCollision"]?.Values<string>()
                        .Where(n => n != null).Select(n => n!).ToList();
                    simList = legacy;
                    deviceList = legacy;
                }
                var sliced = new DepModuleCollisionDetector.SlicedCollisionResult(
                    (IReadOnlyList<string>?)simList ?? Array.Empty<string>(),
                    (IReadOnlyList<string>?)deviceList ?? Array.Empty<string>());

                logger.LogInformation("Loaded wrapper context from {Path}", contextPath);
                return (internalTypeNames, moduleNameForCollision, nestedTypes, sliced);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to load wrapper context: {Message}", ex.Message);
                return (null, null, null, emptyCollisions);
            }
        }

        /// <summary>
        /// Resolves the symbol graph path for doc comment generation.
        /// Priority: explicit --symbolgraph > --no-docs suppression > auto-extraction.
        /// </summary>
        /// <param name="explicitSymbolGraph">Explicit --symbolgraph path from CLI.</param>
        /// <param name="noDocs">True if --no-docs was passed.</param>
        /// <param name="resolution">XCFramework resolution (null in manual mode).</param>
        /// <param name="outputDirectory">Output directory for auto-extracted files.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        internal static string? ResolveSymbolGraphPath(
            string? explicitSymbolGraph, bool noDocs,
            XCFrameworkResolution? resolution, string outputDirectory,
            ILogger logger, ICommandRunner? commandRunner = null,
            PlatformInfo? platformInfo = null)
        {
            // 1. Explicit --symbolgraph always wins
            if (!string.IsNullOrWhiteSpace(explicitSymbolGraph))
                return explicitSymbolGraph;

            // 2. --no-docs disables auto-extraction
            if (noDocs)
                return null;

            // 3. Auto-extract (xcframework mode only)
            if (resolution == null)
                return null;

            return SymbolGraphExtractor.Extract(resolution, outputDirectory, logger, commandRunner, platformInfo);
        }

        /// <summary>
        /// Collects internal (non-public) type names from a parsed module.
        /// Returns both short names and module-qualified names for word-boundary matching.
        /// Short names that collide with public type names are removed to avoid over-stripping.
        /// </summary>
        internal static HashSet<string> CollectInternalTypeNames(ModuleDecl module)
        {
            var internalNames = new HashSet<string>();
            var publicNames = new HashSet<string>();
            CollectTypeNames(module.Types, internalNames, publicNames, module.Name);
            // Remove short names that collide with public type names to avoid over-stripping
            internalNames.ExceptWith(publicNames);
            return internalNames;
        }

        private static void CollectTypeNames(IEnumerable<TypeDecl> types, HashSet<string> internalNames, HashSet<string> publicNames, string moduleName)
        {
            foreach (var t in types)
            {
                // Skip types from other modules (e.g., Swift.Error, Foundation.PropertyListDecoder).
                // The ABI JSON includes type descriptors for cross-module extensions, but these
                // types are not internal to this module — they're imports or stdlib types.
                if (t.SwiftTypeName != null && t.SwiftTypeName.Module != moduleName)
                {
                    CollectTypeNames(t.Types, internalNames, publicNames, moduleName);
                    continue;
                }

                if (t.IsModuleInternal)
                {
                    // Always add qualified name (unique, no collision risk)
                    if (t.SwiftTypeName != null)
                        internalNames.Add(t.SwiftTypeName.ToString());
                    // Add short name tentatively (may be removed if it collides with a public name)
                    internalNames.Add(t.Name);
                }
                else
                {
                    publicNames.Add(t.Name);  // Track public short names for collision detection
                }
                CollectTypeNames(t.Types, internalNames, publicNames, moduleName);  // Recurse ALL children
            }
        }

        /// <summary>
        /// Collects underscore-prefixed type names to suppress from C# output.
        /// Types with a leading underscore are considered internal implementation details
        /// unless they are structurally required (e.g., as a superclass of a non-underscore type)
        /// or explicitly kept via the override set.
        /// </summary>
        /// <param name="module">The parsed module declaration.</param>
        /// <param name="keepUnderscoreTypes">
        /// Optional set of module-qualified names to exempt from suppression.
        /// When non-null, any underscore-prefixed type in this set is preserved.
        /// </param>
        /// <returns>Set of module-qualified type names to suppress.</returns>
        internal static HashSet<string> CollectUnderscoreSuppressedTypeNames(
            ModuleDecl module, HashSet<string>? keepUnderscoreTypes = null)
        {
            var underscoreTypes = new HashSet<string>();
            var structurallyRequired = new HashSet<string>();

            CollectUnderscoreTypeNames(module.Types, underscoreTypes, structurallyRequired);

            // Remove structurally required types (superclasses/protocols of non-underscore types)
            underscoreTypes.ExceptWith(structurallyRequired);

            // Remove explicitly kept types
            if (keepUnderscoreTypes != null)
                underscoreTypes.ExceptWith(keepUnderscoreTypes);

            return underscoreTypes;
        }

        private static void CollectUnderscoreTypeNames(
            IEnumerable<TypeDecl> types,
            HashSet<string> underscoreTypes,
            HashSet<string> structurallyRequired)
        {
            foreach (var t in types)
            {
                var qualifiedName = t.SwiftTypeName?.ToString();
                if (qualifiedName != null && t.Name.StartsWith("_"))
                {
                    underscoreTypes.Add(qualifiedName);
                }

                // Check if this non-underscore type references underscore-prefixed types
                if (!t.Name.StartsWith("_"))
                {
                    // Superclass references (ClassDecl only)
                    if (t is ClassDecl classDecl && classDecl.DirectSuperclassName != null
                        && classDecl.DirectSuperclassName.Contains("._"))
                    {
                        structurallyRequired.Add(classDecl.DirectSuperclassName);
                    }

                    // Protocol conformance references
                    var conformances = t switch
                    {
                        ClassDecl cd => cd.Conformances,
                        StructDecl sd => sd.Conformances,
                        EnumDecl ed => ed.Conformances,
                        _ => null
                    };
                    if (conformances != null)
                    {
                        foreach (var conf in conformances)
                        {
                            var protoName = conf.Protocol.ToString();
                            if (protoName.Contains("._"))
                            {
                                structurallyRequired.Add(protoName);
                            }
                        }
                    }
                }

                // Recurse into nested types
                CollectUnderscoreTypeNames(t.Types, underscoreTypes, structurallyRequired);
            }
        }

        /// <summary>
        /// Validates and resolves --framework-dependency paths into FrameworkDependencyInfo objects.
        /// Returns null if validation fails (error already logged).
        /// </summary>
        /// <param name="dependencyPaths">Paths from --framework-dependency CLI options.</param>
        /// <param name="primaryResolution">Primary xcframework resolution (for module name checks).</param>
        /// <param name="primaryXcframeworkPath">Path to the primary xcframework (for self-reference check).</param>
        /// <param name="wrapperArchitectures">Normalized wrapper-architectures value (simulator/device/all).</param>
        /// <param name="primaryPlatformTarget">The primary --platform-target (determines which slice to resolve first).</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        internal static List<FrameworkDependencyInfo>? ResolveFrameworkDependencies(
            string[] dependencyPaths,
            XCFrameworkResolution primaryResolution,
            string primaryXcframeworkPath,
            string wrapperArchitectures,
            XCFrameworkPlatformTarget primaryPlatformTarget,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            PlatformInfo? platformInfo = null)
        {
            var resolvedDeps = new List<FrameworkDependencyInfo>();
            var seenModules = new Dictionary<string, string>(StringComparer.Ordinal); // module → path

            foreach (var depPath in dependencyPaths)
            {
                // Validate path exists
                if (!Directory.Exists(depPath))
                {
                    logger.LogError("Error: --framework-dependency path does not exist: '{Path}'.", depPath);
                    return null;
                }

                // Validate it's an xcframework
                if (!depPath.EndsWith(".xcframework", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("Error: --framework-dependency path is not an xcframework: '{Path}'.", depPath);
                    return null;
                }

                // Validate not the primary xcframework
                var depFullPath = Path.GetFullPath(depPath);
                var primaryFullPath = Path.GetFullPath(primaryXcframeworkPath);
                if (string.Equals(depFullPath, primaryFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("Error: Primary xcframework cannot be listed as a dependency.");
                    return null;
                }

                // Resolve the primary-matching slice first (for module name + search path + dylib for version)
                // This matches the primary platform target so device-only workflows don't fail
                // when the dependency lacks a simulator slice.
                string? simSearchPath = null;
                string? deviceSearchPath = null;
                string moduleName;
                string depDylibPath;
                string? depAbiJsonPath;
                string? depTbdPath;

                try
                {
                    var primaryDepResolution = XCFrameworkResolver.Resolve(
                        depPath, Path.GetTempPath(),
                        primaryPlatformTarget, logger, commandRunner, platformInfo: platformInfo,
                        companionFrameworkPaths: dependencyPaths);
                    moduleName = primaryDepResolution.ModuleName;
                    depDylibPath = primaryDepResolution.DylibPath;
                    depAbiJsonPath = primaryDepResolution.AbiJsonPath;
                    depTbdPath = primaryDepResolution.TbdPath;

                    if (primaryDepResolution.IsSimulatorSlice)
                        simSearchPath = primaryDepResolution.FrameworkSearchPath;
                    else
                        deviceSearchPath = primaryDepResolution.FrameworkSearchPath;
                }
                catch (Exception ex) when (ex is SwiftModuleNotFoundException or StaticLibraryException)
                {
                    // Attempt ObjC-only framework fallback — resolves search path + validates modulemap
                    var objcResolution = XCFrameworkResolver.ResolveObjCFramework(
                        depPath, primaryPlatformTarget, logger, platformInfo: platformInfo);

                    if (objcResolution == null)
                    {
                        // Neither Swift nor ObjC module found. This may be a wrapper xcframework
                        // (compiled binding wrapper) that only needs -F search paths for linking.
                        // Extract search paths from the xcframework directory structure.
                        var wrapperFallback = XCFrameworkResolver.ResolveSearchPathsOnly(
                            depPath, wrapperArchitectures, logger, platformInfo: platformInfo);
                        if (wrapperFallback != null)
                        {
                            var wrapperModuleName = Path.GetFileNameWithoutExtension(depPath);
                            logger.LogInformation(
                                "Dependency '{Name}' is a binary-only framework (wrapper) — adding framework search paths.",
                                wrapperModuleName);
                            resolvedDeps.Add(wrapperFallback);
                            continue;
                        }

                        logger.LogError(
                            "Error: Dependency '{Path}' has no Swift module and no ObjC module.modulemap. " +
                            "It may be a Swift framework without library evolution support.",
                            depPath);
                        return null;
                    }

                    logger.LogInformation(
                        "Dependency '{Name}' is ObjC-only — adding framework search path for module resolution.",
                        objcResolution.ModuleName);

                    // Map search path to correct sim/device bucket based on actual selected slice
                    string? simPath = null, devicePath = null;
                    if (objcResolution.IsSimulatorSlice)
                        simPath = objcResolution.FrameworkSearchPath;
                    else
                        devicePath = objcResolution.FrameworkSearchPath;

                    // Resolve opposite or required slice — mirrors Swift dep error logic.
                    // Derive oppositeTarget from actual resolved slice (not requested target)
                    // because SelectSlice can fall back to the other platform variant.
                    var requiresSeparatePlatformSlices = platformInfo?.HasSimulatorVariant ?? true;
                    if (requiresSeparatePlatformSlices && wrapperArchitectures == "all")
                    {
                        var oppositeTarget = objcResolution.IsSimulatorSlice
                            ? XCFrameworkPlatformTarget.Device
                            : XCFrameworkPlatformTarget.Simulator;
                        var oppositeResolution = XCFrameworkResolver.ResolveObjCFramework(
                            depPath, oppositeTarget, logger, platformInfo: platformInfo);
                        var expectSimulator = oppositeTarget == XCFrameworkPlatformTarget.Simulator;
                        if (oppositeResolution != null && oppositeResolution.IsSimulatorSlice == expectSimulator)
                        {
                            if (oppositeResolution.IsSimulatorSlice)
                                simPath = oppositeResolution.FrameworkSearchPath;
                            else
                                devicePath = oppositeResolution.FrameworkSearchPath;
                        }
                        else
                        {
                            logger.LogError(
                                "Error: ObjC dependency '{Path}' lacks required {Target} slice.",
                                depPath, oppositeTarget.ToString().ToLowerInvariant());
                            return null;
                        }
                    }
                    else if (requiresSeparatePlatformSlices && wrapperArchitectures == "device" && simPath != null && devicePath == null)
                    {
                        // Primary resolved simulator but we need device
                        var deviceResolution = XCFrameworkResolver.ResolveObjCFramework(
                            depPath, XCFrameworkPlatformTarget.Device, logger, platformInfo: platformInfo);
                        if (deviceResolution != null && !deviceResolution.IsSimulatorSlice)
                            devicePath = deviceResolution.FrameworkSearchPath;
                        else
                        {
                            logger.LogError(
                                "Error: ObjC dependency '{Path}' lacks required device slice.",
                                depPath);
                            return null;
                        }
                    }
                    else if (requiresSeparatePlatformSlices && wrapperArchitectures == "simulator" && devicePath != null && simPath == null)
                    {
                        // Primary resolved device but we need simulator
                        var simResolution = XCFrameworkResolver.ResolveObjCFramework(
                            depPath, XCFrameworkPlatformTarget.Simulator, logger, platformInfo: platformInfo);
                        if (simResolution != null && simResolution.IsSimulatorSlice)
                            simPath = simResolution.FrameworkSearchPath;
                        else
                        {
                            logger.LogError(
                                "Error: ObjC dependency '{Path}' lacks required simulator slice.",
                                depPath);
                            return null;
                        }
                    }

                    // Skip duplicate module names
                    if (seenModules.TryGetValue(objcResolution.ModuleName, out var existingObjCPath))
                    {
                        logger.LogDebug(
                            "Skipping duplicate dependency module '{Module}' (already resolved from '{Path}').",
                            objcResolution.ModuleName, existingObjCPath);
                        continue;
                    }

                    // Primary module conflict check
                    if (string.Equals(objcResolution.ModuleName, primaryResolution.ModuleName,
                        StringComparison.Ordinal))
                    {
                        logger.LogError(
                            "Error: Primary module '{Module}' cannot be listed as a dependency.",
                            objcResolution.ModuleName);
                        return null;
                    }

                    seenModules[objcResolution.ModuleName] = depPath;
                    resolvedDeps.Add(new FrameworkDependencyInfo
                    {
                        XCFrameworkPath = depFullPath,
                        ModuleName = objcResolution.ModuleName,
                        SimulatorFrameworkSearchPath = simPath,
                        DeviceFrameworkSearchPath = devicePath,
                        IsObjCOnly = true
                    });
                    continue;
                }
                catch (Exception ex)
                {
                    logger.LogError("Error resolving dependency xcframework '{Path}': {Message}",
                        depPath, ex.Message);
                    return null;
                }

                // Skip duplicate module names (can occur when SDK targets pass both
                // ProjectReference-resolved paths and explicit SwiftFrameworkDependency items)
                if (seenModules.TryGetValue(moduleName, out var existingPath))
                {
                    logger.LogDebug(
                        "Skipping duplicate dependency module '{Module}' (already resolved from '{Path}').",
                        moduleName, existingPath);
                    continue;
                }

                // Check primary module as dependency
                if (string.Equals(moduleName, primaryResolution.ModuleName, StringComparison.Ordinal))
                {
                    logger.LogError("Error: Primary module '{Module}' cannot be listed as a dependency.", moduleName);
                    return null;
                }

                seenModules[moduleName] = depPath;

                // Resolve the opposite slice if wrapper-architectures requires both
                var needsSeparatePlatformSlices = platformInfo?.HasSimulatorVariant ?? true;
                if (needsSeparatePlatformSlices && wrapperArchitectures == "all")
                {
                    // Need both slices — resolve whichever the primary didn't give us
                    var oppositeTarget = primaryPlatformTarget == XCFrameworkPlatformTarget.Simulator
                        ? XCFrameworkPlatformTarget.Device
                        : XCFrameworkPlatformTarget.Simulator;
                    try
                    {
                        var oppositeResolution = XCFrameworkResolver.Resolve(
                            depPath, Path.GetTempPath(),
                            oppositeTarget, logger, commandRunner, platformInfo: platformInfo,
                            companionFrameworkPaths: dependencyPaths);

                        if (oppositeResolution.IsSimulatorSlice)
                            simSearchPath = oppositeResolution.FrameworkSearchPath;
                        else
                            deviceSearchPath = oppositeResolution.FrameworkSearchPath;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            "Error: Dependency xcframework '{Path}' lacks required {Target} slice: {Message}",
                            depPath, oppositeTarget.ToString().ToLowerInvariant(), ex.Message);
                        return null;
                    }
                }
                else if (needsSeparatePlatformSlices && wrapperArchitectures == "device" && simSearchPath != null && deviceSearchPath == null)
                {
                    // Primary resolved simulator but we need device for compilation
                    try
                    {
                        var deviceResolution = XCFrameworkResolver.Resolve(
                            depPath, Path.GetTempPath(),
                            XCFrameworkPlatformTarget.Device, logger, commandRunner, platformInfo: platformInfo,
                            companionFrameworkPaths: dependencyPaths);
                        deviceSearchPath = deviceResolution.FrameworkSearchPath;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            "Error: Dependency xcframework '{Path}' lacks required device slice: {Message}",
                            depPath, ex.Message);
                        return null;
                    }
                }
                else if (needsSeparatePlatformSlices && wrapperArchitectures == "simulator" && deviceSearchPath != null && simSearchPath == null)
                {
                    // Primary resolved device but we need simulator for compilation
                    try
                    {
                        var simResolution = XCFrameworkResolver.Resolve(
                            depPath, Path.GetTempPath(),
                            XCFrameworkPlatformTarget.Simulator, logger, commandRunner, platformInfo: platformInfo,
                            companionFrameworkPaths: dependencyPaths);
                        simSearchPath = simResolution.FrameworkSearchPath;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            "Error: Dependency xcframework '{Path}' lacks required simulator slice: {Message}",
                            depPath, ex.Message);
                        return null;
                    }
                }

                // Extract version from dependency using the resolved dylib path
                string? packageVersion = null;
                try
                {
                    var metadata = XCFrameworkMetadataExtractor.Extract(
                        depDylibPath, depPath, moduleName, logger, commandRunner);
                    packageVersion = metadata.IsVersionPlaceholder ? null : metadata.PackageVersion;

                    if (metadata.IsVersionPlaceholder)
                    {
                        logger.LogWarning(
                            "SWIFTBIND021: Dependency '{Module}' has a placeholder version. " +
                            "The generated PackageReference will use '0.0.0'. Update before publishing.",
                            moduleName);
                    }
                }
                catch
                {
                    // Version extraction failure is non-fatal — use placeholder
                    logger.LogWarning(
                        "SWIFTBIND021: Could not extract version from dependency '{Module}'. " +
                        "Using placeholder '0.0.0'.", moduleName);
                }

                resolvedDeps.Add(new FrameworkDependencyInfo
                {
                    XCFrameworkPath = depFullPath,
                    ModuleName = moduleName,
                    PackageVersion = packageVersion,
                    SimulatorFrameworkSearchPath = simSearchPath,
                    DeviceFrameworkSearchPath = deviceSearchPath,
                    DylibPath = depDylibPath,
                    AbiJsonPath = depAbiJsonPath,
                    TbdPath = depTbdPath
                });
            }

            return resolvedDeps;
        }

        /// <summary>
        /// Evaluates wrapper compilation outcome with SDK-mode awareness.
        /// Returns exit code, optional diagnostic code, and message for logging.
        /// </summary>
        internal static (int exitCode, string? diagnosticCode, string message) HandleWrapperCompilationOutcome(
            WrapperCompilationOutcome rawOutcome, bool sdkMode,
            Exception? compilationException, SwiftWrapperCompilationResult? compilationResult)
        {
            var effective = SwiftWrapperCompiler.EffectiveOutcome(rawOutcome, sdkMode);

            if (effective == WrapperCompilationOutcome.Fatal)
            {
                // When the wrapper-link failure already carried precise system-framework/library
                // link guidance (--link-framework / <SwiftLinkFramework>, or the library-only
                // --link-library / <SwiftLinkLibrary> form), the generic "missing dependency
                // framework" causes would contradict it — the author may already have supplied the
                // --framework-dependency — so let the precise guidance stand alone instead of
                // appending a misleading cause.
                var message = compilationException != null
                    ? (HasSystemLinkGuidance(compilationException.Message)
                        ? $"Swift wrapper compilation failed: {compilationException.Message}"
                        : $"Swift wrapper compilation failed: {compilationException.Message}. " +
                          "Generated C# references the wrapper library but no compiled wrapper exists. " +
                          "Common causes: missing dependency framework (use --framework-dependency or <SwiftFrameworkDependency>), " +
                          "or internal types in the library's API. See Troubleshooting docs for details.")
                    : $"All Swift wrapper code was stripped as broken ({compilationResult?.StrippedBlockCount ?? 0} block(s)). " +
                      "Generated C# references the wrapper library but no compiled wrapper exists. " +
                      "Use --async-library explicitly or report this as a generator bug.";
                return (1, null, message);
            }

            if (rawOutcome == WrapperCompilationOutcome.Fatal && sdkMode)
            {
                // Downgraded from Fatal → Warning in SDK mode
                const string actionableHint =
                    " Common causes: missing dependency framework (use --framework-dependency or <SwiftFrameworkDependency>), " +
                    "or internal types in the library's API. See Troubleshooting docs for details.";
                // Suppress the generic causes when precise --link-framework/--link-library guidance
                // is already present, so it isn't undercut by a contradictory "missing dependency"
                // message.
                var exceptionHint = HasSystemLinkGuidance(compilationException?.Message)
                    ? string.Empty
                    : actionableHint;
                var message = compilationException != null
                    ? $"SWIFTBIND050: Swift wrapper compilation failed: {compilationException.Message}. " +
                      "C# bindings are still valid — wrapper-dependent methods will throw DllNotFoundException at runtime." +
                      exceptionHint
                    : $"SWIFTBIND050: All Swift wrapper code was stripped as broken ({compilationResult?.StrippedBlockCount ?? 0} block(s)). " +
                      "C# bindings are still valid — wrapper-dependent methods will throw DllNotFoundException at runtime." +
                      actionableHint;
                return (0, "SWIFTBIND050", message);
            }

            if (effective == WrapperCompilationOutcome.Warning)
            {
                var message = compilationException != null
                    ? $"Swift wrapper compilation failed: {compilationException.Message}"
                    : $"All Swift wrapper code was stripped as broken ({compilationResult?.StrippedBlockCount ?? 0} block(s)).";
                return (0, null, message);
            }

            return (0, null, "");
        }

        /// <summary>
        /// True when a wrapper-compilation failure message already carries the precise
        /// system-framework/library link remediation (<c>--link-framework</c> /
        /// <c>&lt;SwiftLinkFramework&gt;</c>, or the library-only <c>--link-library</c> /
        /// <c>&lt;SwiftLinkLibrary&gt;</c> form — a static archive can need only libc++), so callers
        /// suppress the generic "missing dependency framework" causes that would otherwise
        /// contradict it.
        /// </summary>
        private static bool HasSystemLinkGuidance(string? message) =>
            message != null &&
            (message.Contains("--link-framework", StringComparison.Ordinal) ||
             message.Contains("--link-library", StringComparison.Ordinal));

        /// <summary>
        /// Formats a SWIFTBIND060 dependency warning message with actionable guidance.
        /// </summary>
        /// <param name="frameworkName">The dependency framework name.</param>
        /// <param name="unresolvedReason">The reason: "missing-slice" or "missing-xcframework".</param>
        internal static string FormatDependencyWarning(string frameworkName, string unresolvedReason)
        {
            // MSBuild SDK guidance: SwiftFrameworkDependency provides build-time framework
            // resolution; a sibling PackageReference (or ProjectReference) is required for
            // NuGet restore — they're independent. SWIFTBIND080 in Sdk.targets uses the same
            // pairing.
            const string sdkGuidanceTail =
                "MSBuild SDK: For NuGet package consumption, declare both items — " +
                "<SwiftFrameworkDependency Include=\"path/to/{0}.xcframework\" " +
                "PackageId=\"{0}.Swift.iOS\" PackageVersion=\"1.0.0\" /> for build-time framework " +
                "resolution, and <PackageReference Include=\"{0}.Swift.iOS\" Version=\"1.0.0\" /> " +
                "so NuGet restores the package. For local source builds, use <ProjectReference> " +
                "to the sibling binding csproj instead. ";

            var sdkGuidance = string.Format(sdkGuidanceTail, frameworkName);

            if (unresolvedReason == "missing-slice")
            {
                return $"SWIFTBIND060: Detected dependency '{frameworkName}' but its xcframework " +
                    "lacks the required platform slice. " +
                    "CLI: Use --framework-dependency to specify a complete xcframework. " +
                    sdkGuidance +
                    "Verify the dependency xcframework contains both device and simulator slices.";
            }
            else
            {
                return $"SWIFTBIND060: Detected dependency '{frameworkName}' but no matching " +
                    $"{frameworkName}.xcframework found. " +
                    "CLI: Use --framework-dependency to specify its location. " +
                    sdkGuidance +
                    "You may need to build the dependency separately or obtain it from the library author.";
            }
        }

        internal static string ResolveNamespacePattern(string? cliNamespacePattern, string? configPath, ILogger logger)
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

        internal static string InferFrameworkName(string dylibPath, string moduleName)
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

        /// <summary>
        /// Scans generated C# files for public type declarations to support mixed framework dedup.
        /// Returns the set of type names emitted by the Swift pipeline.
        /// </summary>
        internal static HashSet<string> CollectSwiftEmittedTypeNames(string outputDirectory)
        {
            var typeNames = new HashSet<string>(StringComparer.Ordinal);
            if (!Directory.Exists(outputDirectory))
                return typeNames;

            // Match: public [unsafe] class|struct|enum|interface NAME
            var pattern = new System.Text.RegularExpressions.Regex(
                @"^\s*public\s+(?:unsafe\s+)?(?:partial\s+)?(?:class|struct|enum|interface)\s+(\w+)",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            foreach (var csFile in Directory.GetFiles(outputDirectory, "*.cs"))
            {
                var content = File.ReadAllText(csFile);
                foreach (System.Text.RegularExpressions.Match match in pattern.Matches(content))
                {
                    typeNames.Add(match.Groups[1].Value);
                }
            }

            return typeNames;
        }

        /// <summary>
        /// Lightweight peek at the moduleName attribute of a module database XML file.
        /// Returns null if the file is malformed or missing the expected structure.
        /// </summary>
        internal static string? PeekModuleNameFromXml(string path)
        {
            try
            {
                using var reader = System.Xml.XmlReader.Create(path);
                while (reader.Read())
                {
                    if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Name == "swifttypedatabase")
                    {
                        var moduleName = reader.GetAttribute("moduleName");
                        return string.IsNullOrWhiteSpace(moduleName) ? null : moduleName;
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Lightweight peek at the module name from an ABI JSON file.
        /// Returns null if the file cannot be parsed.
        /// </summary>
        internal static string? PeekModuleNameFromAbiJson(string abiPath)
        {
            try
            {
                var text = File.ReadAllText(abiPath);
                var json = JObject.Parse(text);
                var rootNode = json["ABIRoot"];
                if (rootNode == null) return null;
                var children = rootNode["children"] as JArray;
                if (children == null || children.Count == 0) return null;

                // Skip compiler-internal __ObjC TypeAlias children that some frameworks
                // (e.g., ActivityKit) emit at the front of the child list when they
                // @_export themselves. Matches SwiftABIParser.GetModuleName() logic.
                string? moduleName = null;
                foreach (var child in children)
                {
                    var name = child?.Value<string>("moduleName");
                    if (string.IsNullOrEmpty(name) || name == "__ObjC") continue;
                    moduleName = name;
                    break;
                }
                moduleName ??= children[0]?.Value<string>("moduleName");
                return string.IsNullOrEmpty(moduleName) || moduleName == "NO_MODULE" ? null : moduleName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Creates and configures a logger factory based on the verbosity level.
        /// </summary>
        /// <param name="verbosity">Verbosity level (0 = No logging, 1 = General information, 2 = Debugging information).</param>
        internal static ILoggerFactory CreateLoggerFactory(int verbosity)
        {
            return LoggerFactory.Create(builder =>
            {
                builder.AddConsole();

                builder.SetMinimumLevel(verbosity switch
                {
                    0 => LogLevel.None,  // No logging
                    1 => LogLevel.Information, // Info and above
                    2 => LogLevel.Debug,    // Debug and above
                    _ => throw new ArgumentOutOfRangeException(nameof(verbosity), $"Invalid verbosity level '{verbosity}'. Valid values: 0 (silent), 1 (info), 2 (debug).")
                });
            });
        }
        /// <summary>
        /// Wraps a swiftinterface parser call with error recovery. If the parser throws,
        /// logs a warning and returns the fallback value so generation continues with
        /// reduced metadata rather than aborting entirely.
        /// </summary>
        internal static T TryParseSwiftInterface<T>(string description, Func<T> parse, Func<T> fallback, ILogger logger, ref int failureCount)
        {
            try
            {
                return parse();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                failureCount++;
                logger.LogWarning("Swiftinterface parsing failed for {Description}: {Message}. Continuing with empty data.", description, ex.Message);
                logger.LogDebug("Stack trace for {Description} parse failure:\n{StackTrace}", description, ex.ToString());
                return fallback();
            }
        }
    }
}
