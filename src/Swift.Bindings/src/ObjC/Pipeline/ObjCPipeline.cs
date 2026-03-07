// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration.ObjC;

public sealed record ObjCPipelineResult(
    int ExitCode,
    ObjCModule? Module,
    string? ErrorMessage,
    string? ApiDefinitionPath = null,
    string? StructsAndEnumsPath = null,
    string? ProjectPath = null);

/// <summary>
/// Orchestrates the ObjC binding pipeline: resolve framework -> invoke clang -> parse AST -> summary.
/// </summary>
public static class ObjCPipeline
{
    public static ObjCPipelineResult Run(
        XCFrameworkResolver.ObjCFrameworkResolution resolution,
        string xcframeworkPath,
        string outputDirectory,
        XCFrameworkPlatformTarget platformTarget,
        ILogger logger,
        ICommandRunner? commandRunner = null,
        string? namespacePattern = null,
        string? packageId = null,
        bool sdkMode = false,
        bool isMixed = false,
        HashSet<string>? excludeTypeNames = null,
        IReadOnlyList<string>? additionalFrameworkSearchPaths = null)
    {
        commandRunner ??= new SystemCommandRunner();

        // 1. Derive framework path using the actual directory name (may differ from ObjC module name)
        var frameworkPath = Path.Combine(resolution.FrameworkSearchPath, $"{resolution.FrameworkDirectoryName}.framework");
        if (!Directory.Exists(frameworkPath))
        {
            return new ObjCPipelineResult(1, null,
                $"Framework directory not found: {frameworkPath}");
        }

        // 2. Find umbrella header
        var invoker = new ClangAstInvoker(commandRunner, logger);
        var headerResult = invoker.FindUmbrellaHeader(frameworkPath, resolution.ModuleName);
        if (headerResult == null)
        {
            return new ObjCPipelineResult(1, null,
                $"Could not locate umbrella header for module '{resolution.ModuleName}' in {frameworkPath}");
        }

        // 3. Invoke clang AST dump
        string json;
        try
        {
            json = invoker.InvokeClangAstDump(
                headerResult.HeaderPath, resolution.FrameworkSearchPath,
                resolution.IsSimulatorSlice, headerResult.ModulemapPath,
                additionalFrameworkSearchPaths);
        }
        catch (Exception ex)
        {
            return new ObjCPipelineResult(1, null, $"Clang AST dump failed: {ex.Message}");
        }

        // 4. Parse AST JSON
        var headersPath = Path.Combine(frameworkPath, "Headers");
        ObjCModule module;
        try
        {
            module = ClangAstParser.Parse(json, resolution.ModuleName, headersPath);
        }
        catch (Exception ex)
        {
            return new ObjCPipelineResult(1, null, $"AST parsing failed: {ex.Message}");
        }

        // 4b. Apply mixed-framework filtering (member-level dedup with category extraction)
        if (excludeTypeNames != null && excludeTypeNames.Count > 0)
        {
            module = FilterForMixedFramework(module, excludeTypeNames, logger);
        }
        else
        {
            // Pure ObjC: clear categories — members are already merged inline into classes.
            // Mixed: FilterForMixedFramework already set Categories to shared-class categories only.
            module = module with { Categories = [] };
        }

        // Post-hoc mixed validation: require at least one ObjC class, protocol, or category.
        if (isMixed && module.Classes.Count == 0 && module.Protocols.Count == 0 && module.Categories.Count == 0)
        {
            logger.LogInformation(
                "Mixed framework '{Module}': no ObjC classes or protocols found — skipping ObjC emission.",
                resolution.ModuleName);
            return new ObjCPipelineResult(0, module, null);
        }

        // 5. Emit bindings
        var namespaceResolver = new NamespacePatternResolver(namespacePattern, resolution.ModuleName);
        var resolvedNamespace = namespaceResolver.ResolveNamespace(resolution.ModuleName);

        // Detect namespace/class name collision: if any class has the same name as the
        // namespace, the MAUI registrar generates code with ambiguous type references (CS0426).
        // Fix by appending "Binding" suffix to the namespace.
        if (module.Classes.Any(c => c.Name == resolvedNamespace))
        {
            logger.LogInformation(
                "Namespace '{Namespace}' collides with class name — using '{Namespace}Binding' to avoid CS0426.",
                resolvedNamespace, resolvedNamespace);
            resolvedNamespace = $"{resolvedNamespace}Binding";
        }

        var apiDefPath = ApiDefinitionEmitter.Emit(module, outputDirectory, resolvedNamespace, logger);
        var structsResult = StructsAndEnumsEmitter.Emit(module, outputDirectory, resolvedNamespace, logger);
        var structsPath = structsResult?.FilePath;

        // Emit .csproj:
        // - sdkMode && !isMixed → skip (SDK IS the binding project)
        // - sdkMode && isMixed → emit (SDK is Swift project, ObjC is separate)
        // - !sdkMode → always emit
        string? projectPath = null;
        if (!sdkMode || isMixed)
        {
            projectPath = ObjCBindingProjectEmitter.Emit(
                new ObjCBindingProjectOptions
                {
                    OutputDirectory = outputDirectory,
                    ModuleName = resolution.ModuleName,
                    SourceXCFrameworkPath = xcframeworkPath,
                    PackageId = packageId,
                }, logger);
        }

        // Emit metadata props for SDK integration (always, regardless of sdkMode)
        ObjCMetadataPropsEmitter.Emit(
            outputDirectory, resolution.ModuleName, xcframeworkPath,
            isMixed ? "Mixed" : "ObjC", logger);

        // 6. Dump summary
        DumpSummary(module, logger);

        return new ObjCPipelineResult(0, module, null, apiDefPath, structsPath, projectPath);
    }

    /// <summary>
    /// Member-level dedup for mixed frameworks: shared classes are dropped from ObjC output,
    /// but their category members are extracted into separate [Category] binding interfaces.
    /// Shared protocols are still dropped entirely.
    /// </summary>
    internal static ObjCModule FilterForMixedFramework(
        ObjCModule module, HashSet<string> swiftTypeNames, ILogger logger)
    {
        var removedClasses = module.Classes.Where(c => swiftTypeNames.Contains(c.Name)).ToList();
        var removedProtocols = module.Protocols.Where(p => swiftTypeNames.Contains(p.Name)).ToList();

        // Extract categories for shared classes from module.Categories (populated at parse time).
        // Copy the owning class's GenericTypeParamNames onto each matching category.
        var sharedClassCategories = new List<ObjCCategoryDecl>();
        var classGenericParams = removedClasses.ToDictionary(c => c.Name, c => c.GenericTypeParamNames);
        foreach (var cat in module.Categories)
        {
            if (swiftTypeNames.Contains(cat.ClassName) && classGenericParams.TryGetValue(cat.ClassName, out var genericParams))
            {
                sharedClassCategories.Add(cat with { GenericTypeParamNames = genericParams });
            }
        }

        if (removedClasses.Count > 0 || removedProtocols.Count > 0)
        {
            logger.LogInformation(
                "Mixed dedup: removed {ClassCount} shared class(es) and {ProtoCount} shared protocol(s) from ObjC output, extracted {CatCount} category interface(s).",
                removedClasses.Count, removedProtocols.Count, sharedClassCategories.Count);
        }

        return module with
        {
            Classes = module.Classes.Where(c => !swiftTypeNames.Contains(c.Name)).ToList(),
            Protocols = module.Protocols.Where(p => !swiftTypeNames.Contains(p.Name)).ToList(),
            Categories = sharedClassCategories,
            // Enums, structs, functions, constants, typedefs are never filtered
        };
    }

    private static void DumpSummary(ObjCModule module, ILogger logger)
    {
        logger.LogInformation("");
        logger.LogInformation("═══ ObjC Module: {Module} ═══", module.ModuleName);
        logger.LogInformation("  Classes:   {Count}", module.Classes.Count);
        logger.LogInformation("  Protocols: {Count}", module.Protocols.Count);
        logger.LogInformation("  Enums:     {Count}", module.Enums.Count);
        logger.LogInformation("  Structs:   {Count}", module.Structs.Count);
        logger.LogInformation("  Functions: {Count}", module.Functions.Count);
        logger.LogInformation("  Constants: {Count}", module.Constants.Count);
        logger.LogInformation("  Typedefs:  {Count}", module.Typedefs.Count);
        if (module.Categories.Count > 0)
            logger.LogInformation("  Categories:{Count}", module.Categories.Count);
        logger.LogInformation("  Total:     {Count}", module.TotalDeclarations);
        logger.LogInformation("");

        foreach (var cls in module.Classes)
        {
            var protocols = cls.ProtocolNames.Count > 0
                ? $" <{string.Join(", ", cls.ProtocolNames)}>"
                : "";
            var super = cls.SuperclassName != null ? $" : {cls.SuperclassName}" : "";
            logger.LogInformation("  {Name}{Super}{Protocols} ({Methods}m, {Properties}p)",
                cls.Name, super, protocols, cls.Methods.Count, cls.Properties.Count);
        }
    }
}
