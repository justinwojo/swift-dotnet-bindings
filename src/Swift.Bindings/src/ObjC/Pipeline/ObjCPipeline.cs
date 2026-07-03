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
/// Output of <see cref="ObjCPipeline.Parse"/>: the parsed-and-eligibility-filtered ObjC module,
/// its resolved companion namespace, the platform info, and accumulated diagnostics. The module
/// has already had the platform-stub and native-symbol class/free-symbol guards applied so that
/// any type-resolution records synthesized from it match exactly the surface the companion will
/// emit — but NOT the mixed-dedup (4b), delegate detection (4e), or foreign-category (4d) filters,
/// which run later in <see cref="ObjCPipeline.FilterAndEmit"/> (4b and 4d need
/// <c>swift-types.json</c>; 4e runs there because it depends on 4b's output). On a non-zero
/// <see cref="ExitCode"/>, <see cref="Module"/> is null and <see cref="ErrorMessage"/> explains why.
/// </summary>
public sealed record ObjCParseResult(
    int ExitCode,
    ObjCModule? Module,
    string? ErrorMessage,
    string ResolvedNamespace,
    PlatformInfo PlatformInfo,
    ObjCBindingDiagnostics Diagnostics);

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
        PlatformInfo? platformInfo = null,
        NativeLinkage sourceNativeLinkage = NativeLinkage.Dynamic,
        bool hasWrapperXCFramework = false)
    {
        var parse = Parse(
            resolution, xcframeworkPath, logger, commandRunner,
            namespacePattern, additionalFrameworkSearchPaths, platformInfo);
        if (parse.ExitCode != 0 || parse.Module == null)
            return new ObjCPipelineResult(parse.ExitCode, parse.Module, parse.ErrorMessage);

        return FilterAndEmit(
            parse.Module, parse.ResolvedNamespace, parse.PlatformInfo, parse.Diagnostics,
            resolution, xcframeworkPath, outputDirectory, logger,
            packageId, sdkMode, isMixed, excludeTypeNames,
            sourceNativeLinkage, hasWrapperXCFramework);
    }

    /// <summary>
    /// Resolve the framework, invoke clang, parse the AST, and apply the eligibility guards that
    /// need neither the Swift pass's <c>swift-types.json</c> ownership manifest nor the earlier
    /// mixed-dedup pass: platform-type stubs (4c) and the native-symbol class (4f) / free-symbol
    /// (4g) guards. The returned module is exactly the surface the companion will emit for those
    /// filters, so a caller may synthesize type-resolution records from it (the mixed ObjC↔Swift
    /// bridge) knowing every record maps to a companion type that survives to emission. The
    /// swift-types.json-dependent mixed dedup (4b) and pure-mode foreign-category filter (4d), plus
    /// delegate-protocol detection (4e) — which runs after 4b so it never marks a protocol off a
    /// class 4b removed — run later in <see cref="FilterAndEmit"/>; the resolved namespace is
    /// computed here (once) and threaded through so a synthesized record's <c>CSharpTypeName</c>
    /// namespace matches the emitted companion's exactly.
    /// </summary>
    public static ObjCParseResult Parse(
        XCFrameworkResolver.ObjCFrameworkResolution resolution,
        string xcframeworkPath,
        ILogger logger,
        ICommandRunner? commandRunner = null,
        string? namespacePattern = null,
        IReadOnlyList<string>? additionalFrameworkSearchPaths = null,
        PlatformInfo? platformInfo = null)
    {
        commandRunner ??= new SystemCommandRunner();
        var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
        var diagnostics = new ObjCBindingDiagnostics();

        // 1. Derive framework path using the actual directory name (may differ from ObjC module name)
        var frameworkPath = Path.Combine(resolution.FrameworkSearchPath, $"{resolution.FrameworkDirectoryName}.framework");
        if (!Directory.Exists(frameworkPath))
        {
            return new ObjCParseResult(1, null,
                $"Framework directory not found: {frameworkPath}", "", pi, diagnostics);
        }

        // 2. Find umbrella header
        var invoker = new ClangAstInvoker(commandRunner, logger);
        var headerResult = invoker.FindUmbrellaHeader(frameworkPath, resolution.ModuleName);
        if (headerResult == null)
        {
            return new ObjCParseResult(1, null,
                $"Could not locate umbrella header for module '{resolution.ModuleName}' in {frameworkPath}",
                "", pi, diagnostics);
        }

        // 3. Invoke clang AST dump
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
            return new ObjCParseResult(1, null, $"Clang AST dump failed: {ex.Message}", "", pi, diagnostics);
        }

        // 4. Parse AST JSON
        var headersPath = Path.Combine(frameworkPath, "Headers");
        ObjCModule module;
        try
        {
            module = ClangAstParser.Parse(json, resolution.ModuleName, headersPath, logger);
        }
        catch (Exception ex)
        {
            return new ObjCParseResult(1, null, $"AST parsing failed: {ex.Message}", "", pi, diagnostics);
        }

        // 4c. Filter out platform type stubs (types already in the Apple SDK)
        module = FilterPlatformTypeStubs(module, logger);

        // 4f. Native-symbol existence guard (Gap 3): drop classes the headers declare but
        // whose `_OBJC_CLASS_$_<Name>` symbol is defined in NO binary the consumer links —
        // header-only "over-bindings" whose ObjC runtime registration / link would fail
        // (the demonstrated case is `_OBJC_CLASS_$_OMIDAdSession` undefined). Union symbols
        // across every shipped slice AND the dependency framework binaries on the search
        // paths, so a device-only/sim-only class, or one defined in a linked dependency, is
        // not false-dropped from the shared ApiDefinition. Fail-open when no binary is
        // readable or no class symbols are found at all (never remove without positive
        // proof of absence). Classes only — protocols use `_OBJC_PROTOCOL_$_`/section
        // metadata, not class symbols, and bgen tolerates protocol-only declarations.
        // This runs in Parse (before the swift-types.json-dependent mixed dedup) so that
        // records synthesized from the parsed module never name a header-only class the
        // companion drops — which would resolve a Swift member to a C# type absent from the
        // companion assembly (CS0246).
        var symbolBinaries = new List<string>();
        symbolBinaries.AddRange(XCFrameworkResolver.EnumerateObjCSliceNativeBinaries(xcframeworkPath, logger));
        symbolBinaries.AddRange(XCFrameworkResolver.EnumerateFrameworkBinariesUnder(additionalFrameworkSearchPaths, logger));
        var symbolScan = NativeSymbolProbe.ScanObjCClassSymbols(symbolBinaries, commandRunner, logger);
        try
        {
            module = FilterToNativeSymbolBackedClasses(module, symbolScan, logger, diagnostics);
        }
        catch (InvalidOperationException ex)
        {
            // Systemic native-symbol probe failure (SWIFTBIND028): fail loud instead of silently
            // keeping every header-declared class. Surfaced as a non-zero parse result.
            return new ObjCParseResult(1, null, ex.Message, "", pi, diagnostics);
        }

        // 4g. Free-symbol existence guard: drop free C functions and extern globals the headers
        // declare but whose underscore-prefixed C symbol is defined in NO probed binary. These are
        // `static inline`/macro helpers and never-exported globals that look bindable in the header
        // but produce an undefined symbol at link (the force-referencing registrar then fails the
        // whole app). The parser already skips structurally-inline functions; this catches symbols
        // that look external in the AST but aren't actually exported (e.g. a FOUNDATION_EXPORT global
        // the binary never defines). Fail open unless the probe positively gathered defined symbols.
        module = FilterToNativeSymbolBackedFreeSymbols(module, symbolScan, logger, diagnostics);

        // 5. Namespace resolution.
        var namespaceResolver = new NamespacePatternResolver(namespacePattern, resolution.ModuleName);
        var resolvedNamespace = namespaceResolver.ResolveNamespace(resolution.ModuleName);

        // Detect namespace/class name collision: if any class has the same name as the
        // namespace, the MAUI registrar generates code with ambiguous type references (CS0426).
        // Fix by appending "Binding" suffix to the namespace. This is computed here (before the
        // mixed dedup) so the namespace is stable across Parse and FilterAndEmit — a synthesized
        // record and the companion must share one namespace. In pure-ObjC mode this class set is
        // identical to the pre-split module (4b never runs), so behavior is unchanged; in mixed
        // mode the only difference is that a Swift-owned class named exactly the namespace (dropped
        // by 4b) can still force the suffix here — a rare, cosmetic namespace rename, never a break.
        if (module.Classes.Any(c => c.Name == resolvedNamespace))
        {
            logger.LogInformation(
                "Namespace '{Namespace}' collides with class name — using '{Namespace}Binding' to avoid CS0426.",
                resolvedNamespace, resolvedNamespace);
            resolvedNamespace = $"{resolvedNamespace}Binding";
        }

        return new ObjCParseResult(0, module, null, resolvedNamespace, pi, diagnostics);
    }

    /// <summary>
    /// Apply the swift-types.json-dependent filters — mixed-framework member-level dedup (4b) and,
    /// for pure-ObjC frameworks, the foreign-category filter (4d) — then emit the ApiDefinition,
    /// structs/enums, .csproj, and metadata props for the module produced by <see cref="Parse"/>.
    /// The <paramref name="resolvedNamespace"/>, <paramref name="pi"/>, and
    /// <paramref name="diagnostics"/> are threaded from <see cref="Parse"/> so emission uses the
    /// same namespace and accumulates diagnostics across both halves.
    /// </summary>
    public static ObjCPipelineResult FilterAndEmit(
        ObjCModule module,
        string resolvedNamespace,
        PlatformInfo pi,
        ObjCBindingDiagnostics diagnostics,
        XCFrameworkResolver.ObjCFrameworkResolution resolution,
        string xcframeworkPath,
        string outputDirectory,
        ILogger logger,
        string? packageId = null,
        bool sdkMode = false,
        bool isMixed = false,
        HashSet<string>? excludeTypeNames = null,
        NativeLinkage sourceNativeLinkage = NativeLinkage.Dynamic,
        bool hasWrapperXCFramework = false)
    {
        // 4b. Apply mixed-framework filtering (member-level dedup with category extraction)
        if (excludeTypeNames != null && excludeTypeNames.Count > 0)
        {
            module = FilterForMixedFramework(module, excludeTypeNames, logger);
        }

        // Post-hoc mixed validation: require at least one ObjC class, protocol, category, or
        // bridgeable enum. A surface that is only a bridgeable enum — NS_ENUM or NS_OPTIONS, both of
        // which now get a synthesized SimpleEnum bridge record — or whose classes were all consumed
        // by mixed dedup must STILL emit the companion: the bridge record resolves a Swift member to
        // that C# enum, so skipping emission here would leave the record pointing at a type absent
        // from the companion assembly (CS0246 at consumer compile).
        if (isMixed && module.Classes.Count == 0 && module.Protocols.Count == 0
            && module.Categories.Count == 0 && module.Enums.Count == 0)
        {
            logger.LogInformation(
                "Mixed framework '{Module}': no ObjC classes, protocols, or enums found — skipping ObjC emission.",
                resolution.ModuleName);
            return new ObjCPipelineResult(0, module, null);
        }

        // 4e. Detect delegate/data-source protocols and mark them with IsDelegateProtocol.
        // (Marks protocols; removes nothing.) This runs AFTER 4b so a protocol is never marked a
        // delegate on the strength of a Swift-owned class that mixed dedup removed from the ObjC
        // surface — the usage-based scan reads class delegate properties, so it must see only the
        // classes the companion actually emits. In pure-ObjC mode 4b is a no-op, so the input set is
        // the same one this filter received pre-split.
        module = DetectDelegateProtocols(module, logger);

        // 4d. For pure ObjC frameworks, filter categories to keep only foreign-type categories.
        // Own-type categories were already merged into their parent classes by the parser.
        // This runs after the native-symbol class guard (4f, in Parse) so that classes with no
        // native symbol have been removed from module.Classes — otherwise categories on those
        // types would be misclassified. It never runs in mixed mode (4b handles categories there).
        if (excludeTypeNames == null || excludeTypeNames.Count == 0)
        {
            module = FilterToForeignCategories(module, logger);
        }

        // 5. Emit bindings
        var apiDefPath = ApiDefinitionEmitter.Emit(module, outputDirectory, resolvedNamespace, logger, diagnostics, pi);
        var structsResult = StructsAndEnumsEmitter.Emit(module, outputDirectory, resolvedNamespace, logger, diagnostics, pi, excludeTypeNames);
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
                    // Gap 2: a mixed framework's Swift wrapper is the sole carrier for a static
                    // source, so the companion must drop its own source NativeReference to avoid
                    // duplicate-registering the ObjC classes. Pure-ObjC bindings have no wrapper
                    // (defaults: Dynamic + no wrapper → reference kept as sole carrier).
                    SourceNativeLinkage = sourceNativeLinkage,
                    HasWrapperXCFramework = hasWrapperXCFramework,
                }, logger);
        }

        // Emit metadata props for SDK integration (always, regardless of sdkMode)
        ObjCMetadataPropsEmitter.Emit(
            outputDirectory, resolution.ModuleName, xcframeworkPath,
            isMixed ? "Mixed" : "ObjC", logger, pi);

        // 6. Dump summary
        DumpSummary(module, logger);
        diagnostics.LogSummary(logger);

        return new ObjCPipelineResult(0, module, null, apiDefPath, structsPath, projectPath);
    }

    /// <summary>
    /// Filters categories to keep only foreign-type categories (base class not defined in this module).
    /// Own-type categories were already merged into their parent classes by the parser.
    /// Foreign-type categories (e.g., NSNull+MOSValue declaring NSNull conforms to MOSValue)
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
    /// Shared protocols are dropped entirely.
    /// <para/>
    /// <paramref name="swiftTypeNames"/> is the set of Objective-C runtime names the Swift
    /// pipeline owns, read from the <c>swift-types.json</c> ownership manifest (Finding 23). It
    /// is matched against the ObjC declaration names (<c>ObjCClassDecl.Name</c> /
    /// <c>ObjCProtocolDecl.Name</c>), which are themselves ObjC runtime names parsed from the
    /// Swift-generated <c>-Swift.h</c> header — so both sides compare in the same naming
    /// universe. This is what lets the protocol leg fire (the manifest carries a Swift
    /// protocol's ObjC name <c>Foo</c>, not its C# projection <c>IFoo</c>) and an
    /// <c>@objc(CustomName)</c> rename match (the manifest carries the custom name).
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
    /// Native-symbol existence guard (Gap 3). Drops any class whose
    /// <c>_OBJC_CLASS_$_&lt;Name&gt;</c> symbol is defined in none of the scanned binaries
    /// (header-only "over-bindings" whose ObjC runtime registration / link would fail),
    /// plus any category targeting a just-dropped class — a
    /// <c>[Category][BaseType(typeof(X))]</c> on a removed class X fails to compile/link.
    /// Protocols are never touched (they use <c>_OBJC_PROTOCOL_$_</c>/section metadata).
    /// <para>
    /// Tri-state evidence handling (Finding 63): a
    /// <see cref="NativeSymbolProbeOutcome.AllFailed"/> scan — binaries existed but every
    /// <c>nm</c> invocation failed — is a <em>systemic</em> failure and is a hard error
    /// (<c>SWIFTBIND028</c>, thrown), because silently failing open there would let header-only
    /// over-bindings through under the very condition (broken/absent <c>nm</c>) the guard exists to
    /// catch. By contrast, <see cref="NativeSymbolProbeOutcome.NothingToProbe"/> (no binary to
    /// read) or a gathered-but-empty scan (no <c>_OBJC_CLASS_$_</c> symbols at all) fails open and
    /// returns the module unchanged — absence of evidence is not evidence of absence, and this
    /// guard only ever removes with positive proof.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The probe outcome is <see cref="NativeSymbolProbeOutcome.AllFailed"/> (systemic <c>nm</c>
    /// failure). <see cref="Run"/> converts this to a non-zero <see cref="ObjCPipelineResult"/>.
    /// </exception>
    internal static ObjCModule FilterToNativeSymbolBackedClasses(
        ObjCModule module,
        NativeSymbolProbe.ObjCClassSymbolScan scan,
        ILogger logger,
        ObjCBindingDiagnostics diagnostics)
    {
        if (scan.Outcome == NativeSymbolProbeOutcome.AllFailed)
        {
            throw new InvalidOperationException(
                "SWIFTBIND028: native-symbol probe systemic failure — one or more framework/" +
                "dependency binaries were present but every `nm` invocation failed, so the ObjC " +
                "over-binding guard cannot establish which classes are link-backed. This usually " +
                "means `nm` is unavailable or its output format changed. Refusing to silently keep " +
                "all classes (which would let header-only over-bindings through under exactly the " +
                "condition this guard exists to catch). Fix the toolchain and re-run.");
        }

        if (scan.Outcome == NativeSymbolProbeOutcome.NothingToProbe || scan.DefinedClassNames.Count == 0)
        {
            logger.LogDebug(
                "Native-symbol guard: {Reason} — keeping all classes (fail-open).",
                scan.Outcome == NativeSymbolProbeOutcome.NothingToProbe
                    ? "no slice/dependency binary existed to probe"
                    : "nm found no _OBJC_CLASS_$_ symbols");
            return module;
        }

        var keptClasses = new List<ObjCClassDecl>(module.Classes.Count);
        var droppedClassNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cls in module.Classes)
        {
            // A class with objc_runtime_name has its symbol under the runtime name, which the
            // JSON AST doesn't expose — so DefinedClassNames (keyed on declared names) can't
            // confirm or refute it. Keep it: the guard only ever drops with positive proof of
            // absence, and we have none here.
            if (scan.DefinedClassNames.Contains(cls.Name) || cls.HasCustomRuntimeName)
            {
                keptClasses.Add(cls);
            }
            else
            {
                droppedClassNames.Add(cls.Name);
                diagnostics.RecordSkip("class", cls.Name, ObjCSkipReason.MissingNativeSymbol,
                    $"no _OBJC_CLASS_${cls.Name} symbol defined in any framework slice or linked dependency");
            }
        }

        // Drop categories whose base class was just dropped. Categories on classes that
        // survive, or on Apple SDK / foreign classes not in this module's class set, are kept.
        var keptCategories = new List<ObjCCategoryDecl>(module.Categories.Count);
        var droppedCategoryCount = 0;
        foreach (var cat in module.Categories)
        {
            if (droppedClassNames.Contains(cat.ClassName))
            {
                droppedCategoryCount++;
                diagnostics.RecordSkip("category", $"{cat.ClassName}+{cat.CategoryName}",
                    ObjCSkipReason.MissingNativeSymbol,
                    $"base class '{cat.ClassName}' has no native class symbol");
            }
            else
            {
                keptCategories.Add(cat);
            }
        }

        if (droppedClassNames.Count == 0 && droppedCategoryCount == 0)
            return module;

        logger.LogWarning(
            "SWIFTBIND054: dropped {ClassCount} over-bound ObjC class(es) with no native " +
            "_OBJC_CLASS_$_ symbol{CatSuffix}: {Names}. These are declared in the framework " +
            "headers but not defined in any shipped slice or linked dependency; binding them " +
            "would fail to link. Review the framework distribution if any are expected.",
            droppedClassNames.Count,
            droppedCategoryCount > 0
                ? $" (and {droppedCategoryCount} dependent category/categories)"
                : "",
            string.Join(", ", droppedClassNames.OrderBy(n => n, StringComparer.Ordinal)));

        return module with { Classes = keptClasses, Categories = keptCategories };
    }

    /// <summary>
    /// Drops free C functions and <c>extern</c> globals whose underscore-prefixed C symbol is
    /// defined in none of the probed binaries — header declarations with no exported symbol
    /// (<c>static inline</c>/<c>NS_INLINE</c> helpers, never-exported <c>FOUNDATION_EXPORT</c>
    /// globals). A generated P/Invoke or <c>[Field]</c> for these is an undefined symbol at link.
    /// Mach-O C symbols carry a leading underscore, so the test is <c>_&lt;name&gt;</c> ∈ defined set.
    /// Fail-open discipline matches the class guard: act only when the probe positively
    /// <see cref="NativeSymbolProbeOutcome.Gathered"/> symbols (an empty/failed/absent scan keeps
    /// everything — absence of evidence is not evidence of absence). Only <c>extern</c> constants
    /// are considered: non-extern constants are emitted as compile-time literals, not symbol-backed
    /// <c>[Field]</c>s, so they need no native symbol.
    /// </summary>
    internal static ObjCModule FilterToNativeSymbolBackedFreeSymbols(
        ObjCModule module,
        NativeSymbolProbe.ObjCClassSymbolScan scan,
        ILogger logger,
        ObjCBindingDiagnostics diagnostics)
    {
        // The class guard already converted an AllFailed outcome into a hard pipeline error, so by
        // the time this runs the outcome is Gathered, NothingToProbe, or AllFailed-but-class-set-empty.
        // Guard explicitly anyway: only a positive Gathered scan with at least one defined symbol is
        // proof enough to drop anything.
        if (scan.Outcome != NativeSymbolProbeOutcome.Gathered || scan.DefinedSymbols.Count == 0)
        {
            logger.LogDebug(
                "Free-symbol guard: no positively-gathered defined symbols — keeping all functions " +
                "and globals (fail-open).");
            return module;
        }

        bool IsExported(string name) => scan.DefinedSymbols.Contains("_" + name);

        var keptFunctions = new List<ObjCFunctionDecl>(module.Functions.Count);
        var droppedNames = new List<string>();
        foreach (var fn in module.Functions)
        {
            if (IsExported(fn.Name))
            {
                keptFunctions.Add(fn);
            }
            else
            {
                droppedNames.Add(fn.Name);
                diagnostics.RecordSkip("function", fn.Name, ObjCSkipReason.MissingNativeSymbol,
                    $"no _{fn.Name} symbol defined in any framework slice or linked dependency " +
                    "(static inline / unexported helper)");
            }
        }

        var keptConstants = new List<ObjCConstantDecl>(module.Constants.Count);
        foreach (var constant in module.Constants)
        {
            if (!constant.IsExtern || IsExported(constant.Name))
            {
                keptConstants.Add(constant);
            }
            else
            {
                droppedNames.Add(constant.Name);
                diagnostics.RecordSkip("constant", constant.Name, ObjCSkipReason.MissingNativeSymbol,
                    $"no _{constant.Name} symbol defined in any framework slice or linked dependency " +
                    "(unexported global)");
            }
        }

        if (droppedNames.Count == 0)
            return module;

        logger.LogWarning(
            "SWIFTBIND055: dropped {Count} free symbol(s) with no native C symbol: {Names}. These are " +
            "declared in the framework headers but exported by no shipped slice or linked dependency " +
            "(static inline helpers or never-exported globals); binding them would fail to link.",
            droppedNames.Count,
            string.Join(", ", droppedNames.OrderBy(n => n, StringComparer.Ordinal)));

        return module with { Functions = keptFunctions, Constants = keptConstants };
    }

    /// <summary>
    /// Filters out classes and protocols that are Apple SDK platform types.
    /// These types are already provided by the .NET iOS bindings and emitting stub
    /// interfaces for them causes conflicts (CS0101, CS0111).
    /// </summary>
    internal static ObjCModule FilterPlatformTypeStubs(ObjCModule module, ILogger logger)
    {
        var appleSdkTypes = module.AppleSdkTypeNamespaces;
        if (appleSdkTypes == null || appleSdkTypes.Count == 0)
            return module;

        var filteredClasses = new List<ObjCClassDecl>();
        var removedClassCount = 0;
        foreach (var cls in module.Classes)
        {
            if (appleSdkTypes.ContainsKey(cls.Name))
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
            if (appleSdkTypes.ContainsKey(proto.Name))
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
