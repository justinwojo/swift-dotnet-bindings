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
        string? packageId = null)
    {
        commandRunner ??= new SystemCommandRunner();

        // 1. Derive framework path
        var frameworkPath = Path.Combine(resolution.FrameworkSearchPath, $"{resolution.ModuleName}.framework");
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
                resolution.IsSimulatorSlice, headerResult.ModulemapPath);
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

        // 5. Emit bindings
        var namespaceResolver = new NamespacePatternResolver(namespacePattern, resolution.ModuleName);
        var resolvedNamespace = namespaceResolver.ResolveNamespace(resolution.ModuleName);

        var apiDefPath = ApiDefinitionEmitter.Emit(module, outputDirectory, resolvedNamespace, logger);
        var structsPath = StructsAndEnumsEmitter.Emit(module, outputDirectory, resolvedNamespace, logger);
        var projectPath = ObjCBindingProjectEmitter.Emit(
            new ObjCBindingProjectOptions
            {
                OutputDirectory = outputDirectory,
                ModuleName = resolution.ModuleName,
                SourceXCFrameworkPath = xcframeworkPath,
                PackageId = packageId,
            }, logger);

        // 6. Dump summary
        DumpSummary(module, logger);

        return new ObjCPipelineResult(0, module, null, apiDefPath, structsPath, projectPath);
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
