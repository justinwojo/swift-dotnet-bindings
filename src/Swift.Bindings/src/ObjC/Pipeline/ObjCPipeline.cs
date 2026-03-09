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
        IReadOnlyList<string>? additionalFrameworkSearchPaths = null,
        PlatformInfo? platformInfo = null)
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
        var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
        string json;
        try
        {
            var sliceVariant = pi.GetSlice(resolution.IsSimulatorSlice);
            json = invoker.InvokeClangAstDump(
                headerResult.HeaderPath, resolution.FrameworkSearchPath,
                resolution.IsSimulatorSlice, headerResult.ModulemapPath,
                additionalFrameworkSearchPaths, sliceVariant);
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

        // Post-hoc mixed validation: require at least one ObjC class, protocol, or category.
        if (isMixed && module.Classes.Count == 0 && module.Protocols.Count == 0 && module.Categories.Count == 0)
        {
            logger.LogInformation(
                "Mixed framework '{Module}': no ObjC classes or protocols found — skipping ObjC emission.",
                resolution.ModuleName);
            return new ObjCPipelineResult(0, module, null);
        }

        // 4c. Filter out platform type stubs (types already in the Apple SDK)
        module = FilterPlatformTypeStubs(module, logger);

        // 4d. For pure ObjC frameworks, filter categories to keep only foreign-type categories.
        // Own-type categories were already merged into their parent classes by the parser.
        // This MUST run after FilterPlatformTypeStubs (4c) so that SDK stub classes
        // (e.g., UIButton, MKAnnotationView) have been removed from module.Classes —
        // otherwise categories on those types would be misclassified as own-type and dropped.
        if (excludeTypeNames == null || excludeTypeNames.Count == 0)
        {
            module = FilterToForeignCategories(module, logger);
        }

        // 4e. Detect delegate/data-source protocols and mark them with IsDelegateProtocol
        module = DetectDelegateProtocols(module, logger);

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

        var diagnostics = new ObjCBindingDiagnostics();
        var apiDefPath = ApiDefinitionEmitter.Emit(module, outputDirectory, resolvedNamespace, logger, diagnostics, pi);
        var structsResult = StructsAndEnumsEmitter.Emit(module, outputDirectory, resolvedNamespace, logger, diagnostics, pi);
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
                    PlatformInfo = pi,
                }, logger);
        }

        // Emit metadata props for SDK integration (always, regardless of sdkMode)
        ObjCMetadataPropsEmitter.Emit(
            outputDirectory, resolution.ModuleName, xcframeworkPath,
            isMixed ? "Mixed" : "ObjC", logger);

        // 6. Dump summary
        DumpSummary(module, logger);
        diagnostics.LogSummary(logger);

        return new ObjCPipelineResult(0, module, null, apiDefPath, structsPath, projectPath);
    }

    /// <summary>
    /// Filters categories to keep only foreign-type categories (base class not defined in this module).
    /// Own-type categories were already merged into their parent classes by the parser.
    /// Foreign-type categories (e.g., NSNull+RLMValue declaring NSNull conforms to RLMValue)
    /// must be preserved and emitted as [Category] binding interfaces.
    /// </summary>
    internal static ObjCModule FilterToForeignCategories(ObjCModule module, ILogger logger)
    {
        var moduleClassNames = new HashSet<string>(module.Classes.Select(c => c.Name));
        var foreignCategories = module.Categories
            .Where(c => !moduleClassNames.Contains(c.ClassName))
            .ToList();

        if (foreignCategories.Count > 0)
        {
            logger.LogInformation(
                "Preserved {Count} foreign-type category interface(s) for [Category] emission.",
                foreignCategories.Count);
        }

        return module with { Categories = foreignCategories };
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

    /// <summary>
    /// Filters out classes and protocols that are Apple SDK platform types.
    /// These types are already provided by the .NET iOS bindings and emitting stub
    /// interfaces for them causes conflicts (CS0101, CS0111).
    /// </summary>
    internal static ObjCModule FilterPlatformTypeStubs(ObjCModule module, ILogger logger)
    {
        var appleSdkTypes = module.AppleSdkTypeNames;
        if (appleSdkTypes == null || appleSdkTypes.Count == 0)
            return module;

        var filteredClasses = new List<ObjCClassDecl>();
        var removedClassCount = 0;
        foreach (var cls in module.Classes)
        {
            if (appleSdkTypes.Contains(cls.Name))
            {
                removedClassCount++;
                logger.LogDebug("Filtering platform type stub class: {Name}", cls.Name);
            }
            else
            {
                filteredClasses.Add(cls);
            }
        }

        var filteredProtocols = new List<ObjCProtocolDecl>();
        var removedProtoCount = 0;
        foreach (var proto in module.Protocols)
        {
            if (appleSdkTypes.Contains(proto.Name))
            {
                removedProtoCount++;
                logger.LogDebug("Filtering platform type stub protocol: {Name}", proto.Name);
            }
            else
            {
                filteredProtocols.Add(proto);
            }
        }

        if (removedClassCount > 0 || removedProtoCount > 0)
        {
            logger.LogInformation(
                "Filtered {ClassCount} platform type stub class(es) and {ProtoCount} protocol(s) already in Apple SDK.",
                removedClassCount, removedProtoCount);
        }

        return module with
        {
            Classes = filteredClasses,
            Protocols = filteredProtocols,
        };
    }

    /// <summary>
    /// Detects delegate/data-source protocols using two heuristics:
    /// (a) Name ends with "Delegate" or "DataSource".
    /// (b) Protocol is used as the type of a delegate/data-source property on any class.
    ///     Matches property names like "delegate", "dataSource", "navigationDelegate",
    ///     "downloadDelegate", "UIDelegate", etc. — any property whose protocol-qualified
    ///     type references a *Delegate or *DataSource protocol.
    /// Sets IsDelegateProtocol = true on matching protocols.
    /// </summary>
    internal static ObjCModule DetectDelegateProtocols(ObjCModule module, ILogger logger)
    {
        // Collect protocol names referenced by delegate/dataSource properties on classes
        var usageBasedDelegateProtocols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cls in module.Classes)
        {
            foreach (var prop in cls.Properties)
            {
                if (IsDelegateProperty(prop))
                {
                    // The property type may be a protocol-qualified id or a concrete protocol name
                    foreach (var protocolName in ExtractProtocolNamesFromType(prop.Type))
                        usageBasedDelegateProtocols.Add(protocolName);
                }
            }
        }

        var anyChanged = false;
        var updatedProtocols = new List<ObjCProtocolDecl>(module.Protocols.Count);
        foreach (var proto in module.Protocols)
        {
            var isDelegate = proto.Name.EndsWith("Delegate", StringComparison.Ordinal)
                          || proto.Name.EndsWith("DataSource", StringComparison.Ordinal)
                          || usageBasedDelegateProtocols.Contains(proto.Name);

            if (isDelegate && !proto.IsDelegateProtocol)
            {
                updatedProtocols.Add(proto with { IsDelegateProtocol = true });
                anyChanged = true;
                logger.LogDebug("Detected delegate protocol: {Name}", proto.Name);
            }
            else
            {
                updatedProtocols.Add(proto);
            }
        }

        return anyChanged ? module with { Protocols = updatedProtocols } : module;
    }

    /// <summary>
    /// Returns true if this property is a delegate or data-source property.
    /// Matches: (a) property named "delegate" or "dataSource" (exact), or
    /// (b) any property whose protocol-qualified type references a protocol
    /// whose name ends with "Delegate" or "DataSource" (e.g., WKNavigationDelegate).
    /// </summary>
    internal static bool IsDelegateProperty(ObjCPropertyDecl prop)
    {
        // Exact match: covers the standard ObjC delegate/dataSource pattern
        if (prop.Name is "delegate" or "dataSource")
            return true;

        // Check if the property's type is protocol-qualified and the protocol
        // name ends with Delegate or DataSource (e.g., id<WKNavigationDelegate>)
        foreach (var protoName in prop.Type.ProtocolQualifications)
        {
            if (protoName.EndsWith("Delegate", StringComparison.Ordinal)
                || protoName.EndsWith("DataSource", StringComparison.Ordinal))
                return true;
        }

        // Check direct pointer type name (non-protocol-qualified)
        if (prop.Type.IsPointer && !string.IsNullOrEmpty(prop.Type.Name)
            && prop.Type.Name != "id" && prop.Type.Name != "NSObject")
        {
            if (prop.Type.Name.EndsWith("Delegate", StringComparison.Ordinal)
                || prop.Type.Name.EndsWith("DataSource", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Common marker protocols that should be skipped when extracting delegate protocol names.
    /// These are conformance markers, not the actual delegate protocol.
    /// </summary>
    private static readonly HashSet<string> MarkerProtocols = new(StringComparer.Ordinal)
    {
        "NSObject", "NSObjectProtocol", "NSCopying", "NSMutableCopying",
        "NSCoding", "NSSecureCoding", "NSFastEnumeration",
    };

    /// <summary>
    /// Extracts delegate/data-source protocol names from a property type reference.
    /// For multi-protocol qualifications (e.g., id&lt;NSObject, MyObserver&gt;), returns all
    /// non-marker protocols so the caller can record each one for usage-based detection.
    /// </summary>
    private static IEnumerable<string> ExtractProtocolNamesFromType(ObjCTypeRef typeRef)
    {
        // Protocol-qualified id: id<SomeDelegate> or id<NSObject, SomeDelegate>
        if (typeRef.ProtocolQualifications.Count > 0)
        {
            var nonMarker = typeRef.ProtocolQualifications
                .Where(p => !MarkerProtocols.Contains(p))
                .ToList();
            // Return all non-marker protocols; if all are markers, return them all as fallback
            return nonMarker.Count > 0 ? nonMarker : typeRef.ProtocolQualifications;
        }

        // Direct protocol name (pointer to protocol type)
        if (typeRef.IsPointer && !string.IsNullOrEmpty(typeRef.Name)
            && typeRef.Name != "id" && typeRef.Name != "NSObject")
            return [typeRef.Name];

        return [];
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
