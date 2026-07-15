// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Async-specific bridge generation for SwiftUI Views with async dependency chains.
/// Explicit support for BlinkIDUXView pattern only.
/// </summary>
public static partial class SwiftUIBridgeEmitter
{
    /// <summary>
    /// Known async View patterns. Maps view name to its async bridge configuration.
    /// v1: Only BlinkIDUXView is supported. Other async Views remain as templates.
    /// </summary>
    private static readonly Dictionary<string, AsyncViewPattern> KnownAsyncPatterns = new()
    {
        ["BlinkIDUX.BlinkIDUXView"] = new AsyncViewPattern(
            ViewName: "BlinkIDUXView",
            SessionClassName: "SBW_BlinkIDUX_BlinkIDUXView_Session",
            ExtraSwiftImports: new[] { "BlinkID" },
            SessionFields: new[]
            {
                new AsyncSessionField("sdk", "BlinkIDSdk"),
                new AsyncSessionField("eventStream", "BlinkIDEventStream"),
                new AsyncSessionField("analyzer", "BlinkIDAnalyzer"),
                new AsyncSessionField("model", "BlinkIDUXModel"),
            },
            FlattenedParams: new[]
            {
                new AsyncFlatParam("licenseKey", AsyncFlatParamKind.String, "String",
                    "IntPtr", null, null),
                new AsyncFlatParam("showIntroductionAlert", AsyncFlatParamKind.Bool, "Int32",
                    "int", "!= 0", "? 1 : 0"),
                new AsyncFlatParam("showHelpButton", AsyncFlatParamKind.Bool, "Int32",
                    "int", "!= 0", "? 1 : 0"),
                new AsyncFlatParam("allowHapticFeedback", AsyncFlatParamKind.Bool, "Int32",
                    "int", "!= 0", "? 1 : 0"),
                new AsyncFlatParam("preferFrontCamera", AsyncFlatParamKind.Bool, "Int32",
                    "int", "!= 0", "? 1 : 0"),
            },
            ConstructionChain: new List<AsyncConstructionStep>
            {
                new("sdkSettings", "BlinkIDSdkSettings", IsAsync: false, Throws: false,
                    new List<ConstructionArg>
                    {
                        new("licenseKey", ConstructionArgKind.FlattenedParam, "licenseKey"),
                    }),
                new("sdk", "BlinkIDSdk", IsAsync: true, Throws: true,
                    new List<ConstructionArg>
                    {
                        new("withSettings", ConstructionArgKind.ChainReference, "sdkSettings"),
                    },
                    FactoryMethod: "createBlinkIDSdk"),
                new("eventStream", "BlinkIDEventStream", IsAsync: false, Throws: false,
                    new List<ConstructionArg>()),
                new("analyzer", "BlinkIDAnalyzer", IsAsync: true, Throws: true,
                    new List<ConstructionArg>
                    {
                        new("sdk", ConstructionArgKind.ChainReference, "sdk"),
                        new("eventStream", ConstructionArgKind.ChainReference, "eventStream"),
                    }),
                new("uxSettings", "ScanningUXSettings", IsAsync: false, Throws: false,
                    new List<ConstructionArg>
                    {
                        new("showIntroductionAlert", ConstructionArgKind.FlattenedParam, "showIntroductionAlert"),
                        new("showHelpButton", ConstructionArgKind.FlattenedParam, "showHelpButton"),
                        new("preferredCameraPosition", ConstructionArgKind.Literal, "preferFrontCameraVal ? .front : .back"),
                        new("allowHapticFeedback", ConstructionArgKind.FlattenedParam, "allowHapticFeedback"),
                    }),
                new("model", "BlinkIDUXModel", IsAsync: false, Throws: false,
                    new List<ConstructionArg>
                    {
                        new("analyzer", ConstructionArgKind.ChainReference, "analyzer"),
                        new("uxSettings", ConstructionArgKind.ChainReference, "uxSettings"),
                    }),
            },
            ResultCallback: new AsyncResultCallbackConfig(
                SourceFieldName: "analyzer",
                AwaitMethodName: "result",
                ResultCases: new (string, int)[]
                {
                    ("completed", 0),
                    ("interrupted", 1),
                    ("cancelled", 2),
                    ("ended", 3),
                }),
            ViewInitArgs: new[]
            {
                new ConstructionArg("viewModel", ConstructionArgKind.ChainReference, "model"),
            }),
    };

    /// <summary>
    /// Checks if a View matches a known async pattern.
    /// Returns the pattern if found, null otherwise.
    /// </summary>
    public static AsyncViewPattern? GetAsyncPattern(string viewName, string moduleName)
    {
        return KnownAsyncPatterns.GetValueOrDefault($"{moduleName}.{viewName}");
    }

    /// <summary>
    /// Detects if a View has an async dependency in its init parameters.
    /// A parameter is an async dependency if it's a non-primitive module type
    /// (not a closure, not a standard library type) that matches a known pattern.
    /// </summary>
    public static bool HasAsyncDependency(ViewBridgeInfo info)
    {
        return KnownAsyncPatterns.ContainsKey($"{info.ModuleName}.{info.ViewName}");
    }

    #region Constructor Ranking

    /// <summary>
    /// Selects the best constructor from a list of candidates for async bridge generation.
    /// Filters: non-failable, non-generic constructors only.
    /// Ranks by: fewest parameters where all are bridgeable, then shallowest async depth, then ABI order.
    /// </summary>
    public static MethodDecl? SelectBestConstructor(
        List<MethodDecl> constructors,
        BridgeContext context)
    {
        var candidates = constructors
            .Where(c => c.IsConstructor && !c.IsFailable && !c.IsGeneric)
            .ToList();

        if (candidates.Count == 0)
            return null;

        // Score each candidate: (paramCount where all bridgeable, asyncDepth, abiIndex)
        // Lower is better for all dimensions.
        MethodDecl? best = null;
        int bestParamCount = int.MaxValue;
        int bestAsyncDepth = int.MaxValue;
        int bestAbiIndex = int.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            var ctor = candidates[i];
            var paramCount = ctor.CSSignature.Count - 1; // exclude return type at index 0

            // Check if all params are bridgeable (leaf or resolvable module type).
            // Module type resolution must be checked FIRST because when TypeDatabase
            // is populated, MapParameterType maps same-module classes to BoundType (leaf),
            // which would hide them from async depth counting.
            bool allBridgeable = true;
            int asyncDepth = 0;
            for (int p = 1; p < ctor.CSSignature.Count; p++)
            {
                var param = ctor.CSSignature[p];

                // Check module type first
                if (param.SwiftTypeSpec is NamedTypeSpec namedSpec && context.ModuleDecl != null)
                {
                    var resolved = ResolveModuleType(namedSpec, context.ModuleDecl);
                    if (resolved != null)
                    {
                        // Check if it has async/throws init (contributes to depth)
                        var resolvedCtors = resolved.Methods.Where(m => m.IsConstructor).ToList();
                        if (resolvedCtors.Any(c2 => c2.IsAsync || c2.Throws))
                            asyncDepth++;
                        continue;
                    }
                }

                // Not a module type — check leaf mapping
                var bridgeParam = MapParameterType(param, context);
                if (bridgeParam != null)
                    continue; // leaf type — bridgeable

                allBridgeable = false;
                break;
            }

            if (!allBridgeable)
                continue;

            // Compare: fewest params > shallowest async > ABI order
            bool isBetter = paramCount < bestParamCount
                || (paramCount == bestParamCount && asyncDepth < bestAsyncDepth)
                || (paramCount == bestParamCount && asyncDepth == bestAsyncDepth && i < bestAbiIndex);

            if (isBetter)
            {
                best = ctor;
                bestParamCount = paramCount;
                bestAsyncDepth = asyncDepth;
                bestAbiIndex = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Resolves a named type spec to a TypeDecl within the same module.
    /// Returns null if the type is not found (cross-module or unknown).
    /// </summary>
    private static TypeDecl? ResolveModuleType(NamedTypeSpec namedSpec, ModuleDecl moduleDecl)
    {
        // Named types come as "Module.TypeName" — match against module types
        var fullName = namedSpec.ToString();
        var expectedPrefix = moduleDecl.Name + ".";

        // Only resolve same-module types
        if (!fullName.StartsWith(expectedPrefix, StringComparison.Ordinal))
            return null;

        var simpleName = fullName.Substring(expectedPrefix.Length);
        return moduleDecl.Types.FirstOrDefault(t => t.Name == simpleName);
    }

    #endregion

    #region Async Pattern Inference

    private const int MaxInferenceDepth = 3;

    /// <summary>
    /// Attempts to infer an async construction pattern for a View by analyzing its
    /// constructor parameters and recursively resolving module-local type dependencies.
    /// Returns null if the view doesn't have async dependencies, has cross-module deps,
    /// or exceeds depth limits.
    /// </summary>
    public static AsyncViewPattern? InferAsyncPattern(
        ViewBridgeInfo view,
        BridgeContext context)
    {
        if (context.ModuleDecl == null)
            return null;

        var ctor = SelectBestConstructor(view.Constructors, context);
        if (ctor == null)
            return null;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<AsyncConstructionStep>();
        var flatParams = new List<AsyncFlatParam>();

        if (!BuildConstructionChain(ctor, context, visited, chain, flatParams, depth: 0, view.ViewName))
            return null;

        // Must have at least one async step to be classified as async
        if (!chain.Any(step => step.IsAsync || step.Throws))
            return null;

        // Collect ExtraSwiftImports from cross-module leaf parameters
        var extraImports = flatParams
            .Where(p => p.SourceModule != null && p.SourceModule != view.ModuleName)
            .Select(p => p.SourceModule!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new AsyncViewPattern(
            ViewName: view.ViewName,
            SessionClassName: $"SBW_{view.ModuleName}_{view.ViewName}_Session",
            ExtraSwiftImports: extraImports,
            SessionFields: chain.Select(s => new AsyncSessionField(s.VariableName, s.SwiftTypeName)).ToArray(),
            FlattenedParams: flatParams.ToArray(),
            ConstructionChain: chain);
    }

    /// <summary>
    /// Recursively builds a construction chain for a constructor's parameters.
    /// Returns false if any parameter can't be resolved (template fallback).
    /// </summary>
    private static bool BuildConstructionChain(
        MethodDecl ctor,
        BridgeContext context,
        HashSet<string> visited,
        List<AsyncConstructionStep> chain,
        List<AsyncFlatParam> flatParams,
        int depth,
        string ownerTypeName)
    {
        if (depth > MaxInferenceDepth)
            return false;

        // CSSignature[0] is the return type; params start at index 1
        for (int i = 1; i < ctor.CSSignature.Count; i++)
        {
            var param = ctor.CSSignature[i];

            // Try to resolve as a same-module named type FIRST.
            // This must happen before MapParameterType because when TypeDatabase is
            // populated, MapParameterType maps same-module classes to BoundType (leaf),
            // which would prevent inference from seeing them as chain dependencies.
            if (param.SwiftTypeSpec is NamedTypeSpec namedSpec && context.ModuleDecl != null)
            {
                var resolved = ResolveModuleType(namedSpec, context.ModuleDecl);
                if (resolved != null)
                {
                    // Same-module type — treat as chain dependency
                    var resolvedCtors = resolved.Methods.Where(m => m.IsConstructor && !m.IsFailable && !m.IsGeneric).ToList();
                    var bestCtor = SelectBestConstructor(resolvedCtors, context);
                    if (bestCtor == null)
                        return false;

                    // Cycle detection: path-scoped (add before recurse, remove after)
                    var ctorIndex = resolvedCtors.IndexOf(bestCtor);
                    var visitKey = $"{resolved.Name}+{ctorIndex}";
                    if (!visited.Add(visitKey))
                        return false; // cycle detected

                    // Recurse into this type's constructor
                    bool recurseOk = BuildConstructionChain(bestCtor, context, visited, chain, flatParams, depth + 1, resolved.Name);
                    visited.Remove(visitKey); // unwind — allows DAG (shared deps across branches)

                    if (!recurseOk)
                        return false;

                    // After recursion, add this type as a chain step
                    var args = new List<ConstructionArg>();
                    for (int p = 1; p < bestCtor.CSSignature.Count; p++)
                    {
                        var innerParam = bestCtor.CSSignature[p];
                        // Check if innerParam is itself a module type (chain ref) or a leaf
                        bool isModuleType = innerParam.SwiftTypeSpec is NamedTypeSpec innerNamed
                            && ResolveModuleType(innerNamed, context.ModuleDecl) != null;
                        // ParamLabel is the View's real Swift argument label — recover the original
                        // name the parser may have rewritten for C# keyword safety (event → _event),
                        // mirroring the leaf-arg path at GetViewInitOverrideArgs. It is emitted bare
                        // at the call site (Swift accepts a keyword argument label without backticks).
                        if (isModuleType)
                        {
                            args.Add(new ConstructionArg(innerParam.GetSwiftName(), ConstructionArgKind.ChainReference, ToVariableName(innerParam)));
                        }
                        else
                        {
                            args.Add(new ConstructionArg(innerParam.GetSwiftName(), ConstructionArgKind.FlattenedParam, innerParam.Name));
                        }
                    }

                    var varName = ToVariableName(param);
                    chain.Add(new AsyncConstructionStep(
                        VariableName: varName,
                        SwiftTypeName: resolved.Name,
                        IsAsync: bestCtor.IsAsync,
                        Throws: bestCtor.Throws,
                        Args: args));
                    continue;
                }
            }

            // Not a same-module type — try leaf mapping (primitives, String, Bool, closures, TypeDB types)
            var bridgeParam = MapParameterType(param, context);
            if (bridgeParam != null)
            {
                var flatParam = BridgeParamToFlatParam(bridgeParam);
                if (flatParam == null)
                    return false; // unsupported leaf kind for async flattening

                // For cross-module types (BoundType/BoundEnum from TypeDatabase),
                // record the source module for ExtraSwiftImports.
                if (flatParam.Kind is AsyncFlatParamKind.BoundType or AsyncFlatParamKind.BoundStruct or AsyncFlatParamKind.BoundEnum
                    && param.SwiftTypeSpec is NamedTypeSpec leafNamedSpec)
                {
                    var sourceModule = ExtractSwiftModule(leafNamedSpec);
                    if (sourceModule != null)
                        flatParam = flatParam with { SourceModule = sourceModule };
                }

                flatParams.Add(flatParam);
                continue;
            }

            // Neither a resolvable module type nor a supported leaf — can't infer
            return false;
        }

        return true;
    }

    /// <summary>
    /// Converts a BridgeParameter (from leaf analysis) to an AsyncFlatParam
    /// for the async Create function signature.
    /// </summary>
    private static AsyncFlatParam? BridgeParamToFlatParam(BridgeParameter bp)
    {
        return bp.Kind switch
        {
            BridgeParameterKind.String => new AsyncFlatParam(
                bp.Name, AsyncFlatParamKind.String, "String", "IntPtr", null, null),
            BridgeParameterKind.Primitive when bp.SwiftConversion == "!= 0" => new AsyncFlatParam(
                bp.Name, AsyncFlatParamKind.Bool, "Int32", "int", "!= 0", "? 1 : 0"),
            BridgeParameterKind.Primitive => new AsyncFlatParam(
                bp.Name, AsyncFlatParamKind.Primitive, bp.SwiftAbiType, bp.CSharpPInvokeType, null, null),
            BridgeParameterKind.BoundType => new AsyncFlatParam(
                bp.Name, AsyncFlatParamKind.BoundType, "UnsafeMutableRawPointer", "IntPtr",
                null, null, BridgeTypeName: bp.BridgeTypeName, CSharpTypeName: bp.CSharpTypeName,
                IsObjCBridgeable: bp.IsObjCBridgeable),
            BridgeParameterKind.BoundStruct => new AsyncFlatParam(
                bp.Name, AsyncFlatParamKind.BoundStruct, "UnsafeMutableRawPointer", "IntPtr",
                null, null, BridgeTypeName: bp.BridgeTypeName, CSharpTypeName: bp.CSharpTypeName,
                IsObjCBridgeable: bp.IsObjCBridgeable),
            BridgeParameterKind.BoundEnum => new AsyncFlatParam(
                bp.Name, AsyncFlatParamKind.BoundEnum, bp.SwiftAbiType, bp.CSharpPInvokeType,
                null, null, BridgeTypeName: bp.BridgeTypeName, CSharpTypeName: bp.CSharpTypeName,
                IsSimpleEnum: bp.IsSimpleEnum),
            _ => null, // Closures, etc. not supported in async flattening yet
        };
    }

    /// <summary>
    /// Extracts the Swift module name from a NamedTypeSpec.
    /// For "OtherModule.SomeClass", returns "OtherModule".
    /// Returns null if no module prefix is present.
    /// </summary>
    private static string? ExtractSwiftModule(NamedTypeSpec? namedSpec)
    {
        if (namedSpec == null) return null;
        var fullName = namedSpec.ToString();
        var dotIndex = fullName.IndexOf('.');
        return dotIndex > 0 ? fullName[..dotIndex] : null;
    }

    /// <summary>
    /// Generates a camelCase variable name from a parameter declaration.
    /// </summary>
    private static string ToVariableName(ArgumentDecl param)
    {
        var name = param.Name;
        if (string.IsNullOrEmpty(name))
            return "arg";
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    #endregion

    #region Async Swift Generation

    internal static void EmitAsyncSwiftBridge(
        StringBuilder sb, string moduleName, ViewBridgeInfo info, AsyncViewPattern pattern,
        ModuleEmissionContext? emissionContext)
    {
        var prefix = $"SBW_{moduleName}_{info.ViewName}";
        var sessionClass = pattern.SessionClassName;
        var handlesVar = $"{prefix}_liveHandles";

        sb.AppendLine($"// --- {info.ViewName} (Async) ---");
        sb.AppendLine();

        // Callback typedefs
        sb.AppendLine($"/// C function pointer: (handle, userData) → called on success.");
        sb.AppendLine($"public typealias {prefix}_ReadyFn = @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer?) -> Void");
        sb.AppendLine($"/// C function pointer: (msgPtr, msgLen, userData) → called on error.");
        sb.AppendLine($"public typealias {prefix}_ErrorFn = @convention(c) (UnsafePointer<UInt8>, Int, UnsafeMutableRawPointer?) -> Void");
        if (pattern.ResultCallback != null)
        {
            sb.AppendLine($"/// C function pointer: (resultCode, userData) → called when operation completes.");
            sb.AppendLine($"public typealias {prefix}_ResultFn = @convention(c) (Int32, UnsafeMutableRawPointer?) -> Void");
        }
        sb.AppendLine();

        EmitDataDrivenSessionClass(sb, prefix, sessionClass, handlesVar, info, pattern);

        // Handle tracking
        sb.AppendLine($"var {handlesVar} = Set<UnsafeMutableRawPointer>()");
        sb.AppendLine();

        // Create function (async factory)
        EmitDataDrivenAsyncCreate(sb, prefix, sessionClass, handlesVar, moduleName, info, pattern, emissionContext);

        // SwiftUI async-pattern GetViewController in `_direct_helper` bucket. Per-view `prefix` + fixed `_GetViewController` suffix — at most one per bridge.
        // GetViewController function
        emissionContext?.TryAddDirectHelperWrapperSymbol($"{prefix}_GetViewController");
        sb.AppendLine($"@_cdecl(\"{prefix}_GetViewController\")");
        sb.AppendLine($"public func {prefix}_GetViewController(");
        sb.AppendLine($"    _ handle: UnsafeMutableRawPointer?");
        sb.AppendLine(") -> UnsafeMutableRawPointer? {");
        sb.AppendLine("    return SBW_onMainThread {");
        sb.AppendLine($"        guard let handle = handle,");
        sb.AppendLine($"              {handlesVar}.contains(handle) else {{ return nil }}");
        sb.AppendLine($"        let session = Unmanaged<{sessionClass}>");
        sb.AppendLine($"            .fromOpaque(handle).takeUnretainedValue()");
        sb.AppendLine($"        return Unmanaged.passUnretained(session.hostingController).toOpaque()");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Free function — thread-aware dispatch + post-release GCHandle disposal.
        // On-main callers (explicit Dispose from `using` / SwiftUI teardown) run inline.
        // Off-main callers (the .NET finalizer thread, plus any future off-main caller)
        // dispatch async so the finalizer thread returns immediately and unblocks
        // GC.WaitForPendingFinalizers; the queued release block runs when the main run
        // loop spins. The postReleaseFreeFn trampoline frees any GCHandles the C# side
        // packed into handleBuffer — invoked strictly AFTER Unmanaged.release so Swift
        // session state cannot dereference a stale handle.
        // SwiftUI async-pattern Free in `_direct_helper` bucket. Per-view `prefix` + fixed `_Free` suffix — at most one per bridge.
        emissionContext?.TryAddDirectHelperWrapperSymbol($"{prefix}_Free");
        sb.AppendLine($"@_cdecl(\"{prefix}_Free\")");
        sb.AppendLine($"public func {prefix}_Free(");
        sb.AppendLine($"    _ handle: UnsafeMutableRawPointer?,");
        sb.AppendLine($"    _ handleBuffer: UnsafeMutableRawPointer?,");
        sb.AppendLine($"    _ handleCount: Int32,");
        sb.AppendLine($"    _ postReleaseFreeFn: UnsafeMutableRawPointer?");
        sb.AppendLine($") {{");
        sb.AppendLine($"    let release: () -> Void = {{");
        sb.AppendLine($"        if let handle = handle, {handlesVar}.remove(handle) != nil {{");
        if (pattern.ResultCallback != null)
        {
            sb.AppendLine($"            let session = Unmanaged<{sessionClass}>.fromOpaque(handle).takeUnretainedValue()");
            sb.AppendLine($"            session.cancelResultMonitor()");
        }
        sb.AppendLine($"            Unmanaged<{sessionClass}>.fromOpaque(handle).release()");
        sb.AppendLine($"        }}");
        sb.AppendLine($"        if let fnPtr = postReleaseFreeFn {{");
        sb.AppendLine($"            typealias FreeFn = @convention(c) (UnsafeMutableRawPointer?, Int32) -> Void");
        sb.AppendLine($"            let fn = unsafeBitCast(fnPtr, to: FreeFn.self)");
        sb.AppendLine($"            fn(handleBuffer, handleCount)");
        sb.AppendLine($"        }}");
        sb.AppendLine($"    }}");
        sb.AppendLine($"    if Thread.isMainThread {{ release() }}");
        sb.AppendLine($"    else {{ DispatchQueue.main.async(execute: release) }}");
        sb.AppendLine($"}}");
        sb.AppendLine();
    }

    /// <summary>
    /// Emits the Session class for data-driven async views. The session receives a
    /// pre-built UIHostingController from the Create function (where both chain outputs
    /// and flattened leaf params are in scope) and stores SessionFields for ARC retention.
    /// Intermediate chain steps that are not in SessionFields are discarded once Create
    /// returns. When ResultCallback is set, emits a resultTask field and the
    /// startResultMonitor / cancelResultMonitor helpers.
    /// </summary>
    private static void EmitDataDrivenSessionClass(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        sb.AppendLine($"final class {sessionClass} {{");
        foreach (var field in pattern.SessionFields)
        {
            sb.AppendLine($"    let {field.SwiftName}: {field.SwiftType}");
        }
        sb.AppendLine($"    let hostingController: UIHostingController<{info.ViewName}>");
        if (pattern.ResultCallback != null)
        {
            sb.AppendLine($"    private var resultTask: Task<Void, Never>?");
        }
        sb.AppendLine();

        // Session init takes session field values + pre-built hosting controller.
        sb.AppendLine($"    @MainActor");
        sb.Append($"    init(");
        var initParams = pattern.SessionFields.Select(f => $"{f.SwiftName}: {f.SwiftType}").ToList();
        initParams.Add($"hostingController: UIHostingController<{info.ViewName}>");
        sb.Append(string.Join(",\n         ", initParams));
        sb.AppendLine(") {");

        foreach (var field in pattern.SessionFields)
        {
            sb.AppendLine($"        self.{field.SwiftName} = {field.SwiftName}");
        }
        sb.AppendLine($"        self.hostingController = hostingController");
        sb.AppendLine("    }");

        if (pattern.ResultCallback != null)
        {
            sb.AppendLine();
            EmitResultMonitor(sb, prefix, sessionClass, handlesVar, pattern.ResultCallback);
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>
    /// Builds the View init argument list. If the pattern supplies an explicit
    /// ViewInitArgs override, uses that directly (needed for dictionary patterns
    /// where constructor metadata is unreliable or the chain variable name differs
    /// from the View's parameter label). Otherwise auto-infers from the View's
    /// first constructor by matching param labels against chain variable names.
    /// Called in the Create function scope where both chain outputs and leaf params exist.
    /// </summary>
    private static List<string> BuildViewInitArgsFromChain(
        ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        if (pattern.ViewInitArgs != null)
        {
            var overrideArgs = new List<string>();
            foreach (var arg in pattern.ViewInitArgs)
            {
                var value = arg.Kind switch
                {
                    ConstructionArgKind.ChainReference => NameProvider.EscapeSwiftKeyword(arg.Value),
                    ConstructionArgKind.FlattenedParam => FormatFlatParamSwiftValue(arg.Value, pattern.FlattenedParams),
                    ConstructionArgKind.FieldAccess => arg.Value,
                    ConstructionArgKind.Literal => arg.Value,
                    _ => arg.Value,
                };
                overrideArgs.Add($"{arg.ParamLabel}: {value}");
            }
            return overrideArgs;
        }

        var args = new List<string>();
        if (info.Constructors.Count == 0)
            return args;

        var chain = pattern.ConstructionChain;
        var ctor = info.Constructors[0];
        // CSSignature[0] is return type; params start at index 1
        for (int i = 1; i < ctor.CSSignature.Count; i++)
        {
            var param = ctor.CSSignature[i];
            // Find matching chain step by variable name
            var step = chain.FirstOrDefault(s => s.VariableName == ToVariableName(param));
            // Label = the View's real Swift argument label: recover the original name the parser
            // may have rewritten for C# keyword safety (event → _event). NOT backtick-escaped —
            // Swift accepts keyword argument labels bare at a call site (escaping would only warn).
            if (step != null)
            {
                args.Add($"{param.GetSwiftName()}: {step.SwiftName}");
            }
            else
            {
                // Leaf param — apply Bool/String conversion via flattened params.
                var varName = ToVariableName(param);
                varName = FormatFlatParamSwiftValue(varName, pattern.FlattenedParams);
                args.Add($"{param.GetSwiftName()}: {varName}");
            }
        }
        return args;
    }

    private static void EmitResultMonitor(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        AsyncResultCallbackConfig config)
    {
        sb.AppendLine($"    @MainActor");
        sb.AppendLine($"    func startResultMonitor(handle: UnsafeMutableRawPointer,");
        sb.AppendLine($"                            resultCallback: {prefix}_ResultFn?,");
        sb.AppendLine($"                            userData: UnsafeMutableRawPointer?) {{");
        sb.AppendLine($"        let monitorSourceRef = {config.SourceFieldName}");
        sb.AppendLine($"        let cb = resultCallback");
        sb.AppendLine($"        let ud = userData");
        sb.AppendLine($"        let sessionHandle = handle");
        sb.AppendLine($"        self.resultTask = Task {{ @MainActor in");
        sb.AppendLine($"            let result = await monitorSourceRef.{config.AwaitMethodName}()");
        sb.AppendLine($"            guard !Task.isCancelled else {{ return }}");
        sb.AppendLine($"            guard {handlesVar}.contains(sessionHandle) else {{ return }}");
        sb.AppendLine($"            let code: Int32");
        sb.AppendLine($"            switch result {{");
        foreach (var (swiftCase, code) in config.ResultCases)
        {
            sb.AppendLine($"            case .{swiftCase}: code = {code}");
        }
        sb.AppendLine($"            @unknown default: code = -1");
        sb.AppendLine($"            }}");
        sb.AppendLine($"            cb?(code, ud)");
        sb.AppendLine($"        }}");
        sb.AppendLine($"    }}");
        sb.AppendLine();
        sb.AppendLine($"    func cancelResultMonitor() {{");
        sb.AppendLine($"        resultTask?.cancel()");
        sb.AppendLine($"        resultTask = nil");
        sb.AppendLine($"    }}");
    }

    /// <summary>
    /// Data-driven async Create function emission.
    /// Iterates the ConstructionChain steps to emit the Swift @_cdecl factory.
    /// </summary>
    private static void EmitDataDrivenAsyncCreate(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        string moduleName, ViewBridgeInfo info, AsyncViewPattern pattern,
        ModuleEmissionContext? emissionContext)
    {
        var chain = pattern.ConstructionChain;

        // The trailing synthetic params (onReady/onError/onResult/userData) can collide with a
        // user-flattened param of the same name → Swift "invalid redeclaration". Dedup the SYNTHETIC
        // names against the flattened param names; the no-collision case yields the original names
        // (zero churn for existing views).
        var asyncNames = new SyntheticNameScope(pattern.FlattenedParams.Select(p => p.Name));
        var readyName = asyncNames.Reserve("onReady");
        var errorName = asyncNames.Reserve("onError");
        var resultName = "onResult";
        if (pattern.ResultCallback != null)
            resultName = asyncNames.Reserve("onResult");
        var udName = asyncNames.Reserve("userData");

        // SwiftUI async-pattern Create in `_direct_helper` bucket. Per-view `prefix` + fixed `_Create` suffix — at most one per bridge.
        // @_cdecl signature with flattened params + callbacks
        emissionContext?.TryAddDirectHelperWrapperSymbol($"{prefix}_Create");
        sb.AppendLine($"@_cdecl(\"{prefix}_Create\")");
        sb.Append($"public func {prefix}_Create(");

        var createParams = new List<string>();
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.String)
            {
                createParams.Add($"_ {param.Name}Ptr: UnsafePointer<UInt8>?");
                createParams.Add($"_ {param.Name}Len: Int");
            }
            else if (param.Kind is AsyncFlatParamKind.BoundType or AsyncFlatParamKind.BoundStruct)
            {
                createParams.Add($"_ {param.Name}Ptr: UnsafeMutableRawPointer");
            }
            else
            {
                createParams.Add($"_ {param.SwiftName}: {param.SwiftAbiType}");
            }
        }
        createParams.Add($"_ {readyName}: {prefix}_ReadyFn?");
        createParams.Add($"_ {errorName}: {prefix}_ErrorFn?");
        if (pattern.ResultCallback != null)
        {
            createParams.Add($"_ {resultName}: {prefix}_ResultFn?");
        }
        createParams.Add($"_ {udName}: UnsafeMutableRawPointer?");

        sb.AppendLine(string.Join(",\n    ", createParams));
        sb.AppendLine(") {");

        // Guard onReady
        sb.AppendLine($"    guard let {readyName} = {readyName} else {{ return }}");
        sb.AppendLine();

        // Copy string parameters eagerly (before Task)
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.String)
            {
                sb.AppendLine($"    let {param.SwiftName}: String");
                sb.AppendLine($"    if let ptr = {param.Name}Ptr, {param.Name}Len > 0 {{");
                sb.AppendLine($"        {param.SwiftName} = String(");
                sb.AppendLine($"            bytes: UnsafeBufferPointer(start: ptr, count: {param.Name}Len),");
                sb.AppendLine($"            encoding: .utf8");
                sb.AppendLine($"        ) ?? \"\"");
                sb.AppendLine($"    }} else {{");
                sb.AppendLine($"        {param.SwiftName} = \"\"");
                sb.AppendLine($"    }}");
                sb.AppendLine();
            }
        }

        // BoundType null-pointer guard + conversion (before Task)
        // Validates non-null pointers eagerly and reports via error callback instead of trapping.
        var boundTypeParams = pattern.FlattenedParams.Where(p => p.Kind is AsyncFlatParamKind.BoundType or AsyncFlatParamKind.BoundStruct).ToList();
        if (boundTypeParams.Count > 0)
        {
            var ptrNames = string.Join(" || ", boundTypeParams.Select(p => $"{p.Name}Ptr == UnsafeMutableRawPointer(bitPattern: 0)"));
            sb.AppendLine($"    if {ptrNames} {{");
            sb.AppendLine($"        if let {errorName} = {errorName} {{");
            sb.AppendLine($"            let msg = \"Null pointer passed for required object parameter\"");
            sb.AppendLine($"            let utf8 = Array(msg.utf8)");
            sb.AppendLine($"            utf8.withUnsafeBufferPointer {{ buf in");
            sb.AppendLine($"                guard let base = buf.baseAddress else {{ return }}");
            sb.AppendLine($"                {errorName}(base, buf.count, {udName})");
            sb.AppendLine($"            }}");
            sb.AppendLine($"        }}");
            sb.AppendLine($"        return");
            sb.AppendLine($"    }}");
            foreach (var param in boundTypeParams)
            {
                if (param.Kind == AsyncFlatParamKind.BoundType)
                    sb.AppendLine($"    let {param.SwiftName} = Unmanaged<{param.BridgeTypeName}>.fromOpaque({param.Name}Ptr).takeUnretainedValue()");
                else if (param.IsObjCBridgeable) // BoundStruct, ObjC-bridgeable
                    // ObjC-bridgeable struct crosses the ABI as an ObjC object pointer.
                    sb.AppendLine($"    let {param.SwiftName} = Unmanaged<AnyObject>.fromOpaque({param.Name}Ptr).takeUnretainedValue() as! {param.BridgeTypeName}");
                else // BoundStruct
                    sb.AppendLine($"    let {param.SwiftName} = {param.Name}Ptr.assumingMemoryBound(to: {param.BridgeTypeName}.self).pointee");
            }
            sb.AppendLine();
        }

        // BoundEnum conversions eagerly (before Task)
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.BoundEnum)
            {
                // Out-of-range raw value reports via onError and returns instead of trapping.
                sb.AppendLine($"    guard let {param.Name}Enum = {param.BridgeTypeName}(rawValue: {param.SwiftName}) else {{");
                sb.AppendLine($"        if let {errorName} = {errorName} {{");
                sb.AppendLine($"            let msg = \"Invalid raw value for {param.BridgeTypeName}\"");
                sb.AppendLine($"            let utf8 = Array(msg.utf8)");
                sb.AppendLine($"            utf8.withUnsafeBufferPointer {{ buf in");
                sb.AppendLine($"                guard let base = buf.baseAddress else {{ return }}");
                sb.AppendLine($"                {errorName}(base, buf.count, {udName})");
                sb.AppendLine($"            }}");
                sb.AppendLine($"        }}");
                sb.AppendLine($"        return");
                sb.AppendLine($"    }}");
            }
        }
        if (pattern.FlattenedParams.Any(p => p.Kind == AsyncFlatParamKind.BoundEnum))
            sb.AppendLine();

        // Bool conversions eagerly (before Task)
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.Bool)
            {
                sb.AppendLine($"    let {param.Name}Val: Bool = {param.SwiftName} != 0");
            }
        }
        if (pattern.FlattenedParams.Any(p => p.Kind == AsyncFlatParamKind.Bool))
            sb.AppendLine();

        // Async Task — build each chain step, then the session + view
        bool hasThrows = chain.Any(s => s.Throws);
        sb.AppendLine("    Task { @MainActor in");
        if (hasThrows)
            sb.AppendLine("        do {");

        var indent = hasThrows ? "            " : "        ";

        // Emit each construction step
        foreach (var step in chain)
        {
            var tryAwait = (step.Throws ? "try " : "") + (step.IsAsync ? "await " : "");

            // Build argument list
            var args = new List<string>();
            foreach (var arg in step.Args)
            {
                var value = arg.Kind switch
                {
                    ConstructionArgKind.ChainReference => NameProvider.EscapeSwiftKeyword(arg.Value),
                    ConstructionArgKind.FlattenedParam => FormatFlatParamSwiftValue(arg.Value, pattern.FlattenedParams),
                    ConstructionArgKind.FieldAccess => arg.Value,
                    ConstructionArgKind.Literal => arg.Value,
                    _ => arg.Value,
                };
                args.Add($"{arg.ParamLabel}: {value}");
            }

            var argStr = args.Count > 0 ? string.Join(", ", args) : "";
            // Constructor call vs static factory method (e.g. BlinkIDSdk.createBlinkIDSdk(...)).
            var constructorOrFactory = step.FactoryMethod != null
                ? $"{step.SwiftTypeName}.{step.FactoryMethod}"
                : step.SwiftTypeName;
            sb.AppendLine($"{indent}let {step.SwiftName} = {tryAwait}{constructorOrFactory}({argStr})");
        }

        sb.AppendLine();

        // Build the View here in Create scope where both chain outputs and
        // flattened leaf params are available (fixes mixed chain + leaf param views)
        var viewInitArgs = BuildViewInitArgsFromChain(info, pattern);
        if (viewInitArgs.Count == 0)
            sb.AppendLine($"{indent}let rootView = {info.ViewName}()");
        else
            sb.AppendLine($"{indent}let rootView = {info.ViewName}({string.Join(", ", viewInitArgs)})");
        sb.AppendLine($"{indent}let hc = UIHostingController(rootView: rootView)");
        sb.AppendLine();

        // Build session — pass session field values (for retention) + pre-built hosting controller.
        // Non-retained intermediate chain steps (e.g. sdkSettings, uxSettings) are dropped here.
        sb.AppendLine($"{indent}let session = {sessionClass}(");
        // Argument LABEL is the bare field name; the VALUE stays backtick-escaped. A keyword
        // escaped as a call-site label warns ("does not need to be escaped in argument list");
        // a bare label still matches the session init's escaped declared label.
        var sessionArgs = pattern.SessionFields.Select(f => $"{indent}    {f.Name}: {f.SwiftName}").ToList();
        sessionArgs.Add($"{indent}    hostingController: hc");
        sb.AppendLine(string.Join(",\n", sessionArgs));
        sb.AppendLine($"{indent})");

        sb.AppendLine($"{indent}let handle = Unmanaged.passRetained(session).toOpaque()");
        sb.AppendLine($"{indent}{handlesVar}.insert(handle)");

        if (pattern.ResultCallback != null)
        {
            sb.AppendLine($"{indent}session.startResultMonitor(");
            sb.AppendLine($"{indent}    handle: handle,");
            sb.AppendLine($"{indent}    resultCallback: {resultName},");
            sb.AppendLine($"{indent}    userData: {udName}");
            sb.AppendLine($"{indent})");
        }

        sb.AppendLine();
        sb.AppendLine($"{indent}{readyName}(handle, {udName})");

        if (hasThrows)
        {
            sb.AppendLine("        } catch {");
            sb.AppendLine($"            if let {errorName} = {errorName} {{");
            sb.AppendLine("                let msg = \"\\(error)\"");
            sb.AppendLine("                let utf8 = Array(msg.utf8)");
            sb.AppendLine("                utf8.withUnsafeBufferPointer { buf in");
            sb.AppendLine("                    guard let base = buf.baseAddress else { return }");
            sb.AppendLine($"                    {errorName}(base, buf.count, {udName})");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>
    /// Formats a flattened parameter value for use in Swift construction step arguments.
    /// Applies Bool conversion (boolVal) and passes other types as-is.
    /// </summary>
    private static string FormatFlatParamSwiftValue(string paramName, AsyncFlatParam[] flatParams)
    {
        var param = flatParams.FirstOrDefault(p => p.Name == paramName);
        if (param != null && param.Kind == AsyncFlatParamKind.Bool)
            return $"{paramName}Val";
        if (param != null && param.Kind == AsyncFlatParamKind.BoundEnum)
            return $"{paramName}Enum";
        // BoundType uses original name (conversion creates local with the same name).
        // The bare reference targets the SwiftName-declared local, so escape Swift keywords.
        return NameProvider.EscapeSwiftKeyword(paramName);
    }

    #endregion

    #region Async C# Generation

    internal static void EmitAsyncCSharpBridge(
        StringBuilder sb, string moduleName, ViewBridgeInfo info, AsyncViewPattern pattern,
        ModuleEmissionContext? emissionContext)
    {
        var prefix = $"SBW_{moduleName}_{info.ViewName}";
        var bridgeLib = $"{moduleName}Bridge";

        // NativeMethods class
        sb.AppendLine($"    internal static partial class {info.ViewName}BridgeNativeMethods");
        sb.AppendLine("    {");
        sb.AppendLine($"        private const string BridgeLib = \"{bridgeLib}\";");
        sb.AppendLine();

        // Create P/Invoke (returns void — async factory) — build params first
        var createPInvokeParams = new List<string>();
        var csParamNames = new List<string>();
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.String)
            {
                createPInvokeParams.Add($"IntPtr {param.Name}Ptr");
                createPInvokeParams.Add($"nint {param.Name}Len");
                csParamNames.Add($"{param.Name}Ptr");
                csParamNames.Add($"{param.Name}Len");
            }
            else
            {
                createPInvokeParams.Add($"{param.CSharpPInvokeType} {param.CSharpName}");
                csParamNames.Add(param.Name);
            }
        }
        // Dedup the synthetic trailing param names against the user-derived
        // extern param identifiers — two params named e.g. `userData` is CS0100. The
        // extern is called positionally, so renaming the synthetic side is sufficient;
        // the no-collision case yields the original names (zero churn for existing views).
        var csTrailingNames = new SyntheticNameScope(csParamNames);
        createPInvokeParams.Add($"IntPtr {csTrailingNames.Reserve("onReady")}");
        createPInvokeParams.Add($"IntPtr {csTrailingNames.Reserve("onError")}");
        if (pattern.ResultCallback != null)
        {
            createPInvokeParams.Add($"IntPtr {csTrailingNames.Reserve("onResult")}");
        }
        createPInvokeParams.Add($"IntPtr {csTrailingNames.Reserve("userData")}");

        foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
        {
            LibraryPath = bridgeLib,
            EntryPoint = $"{prefix}_Create",
            MethodName = "Create",
            ReturnType = "void",
            ParametersString = string.Join(", ", createPInvokeParams),
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal,
            EmissionContext = emissionContext,
            EnforceWrapperContract = true
        }))
            sb.AppendLine($"        {line}");
        sb.AppendLine();

        // GetViewController P/Invoke
        foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
        {
            LibraryPath = bridgeLib,
            EntryPoint = $"{prefix}_GetViewController",
            MethodName = "GetViewController",
            ReturnType = "IntPtr",
            ParametersString = "IntPtr handle",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal,
            EmissionContext = emissionContext,
            EnforceWrapperContract = true
        }))
            sb.AppendLine($"        {line}");
        sb.AppendLine();

        // Free P/Invoke — widened to carry a GCHandle buffer + post-release trampoline
        // so Swift owns disposal ordering (the trampoline runs strictly AFTER
        // Unmanaged.release inside the dispatched block, closing the use-after-free
        // window where queued main-thread work could dereference a stale handle).
        foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
        {
            LibraryPath = bridgeLib,
            EntryPoint = $"{prefix}_Free",
            MethodName = "Free",
            ReturnType = "void",
            ParametersString = "IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn",
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal,
            EmissionContext = emissionContext,
            EnforceWrapperContract = true
        }))
            sb.AppendLine($"        {line}");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Session class with CreateAsync factory
        sb.AppendLine($"    public sealed class {info.ViewName}Session : IDisposable");
        sb.AppendLine("    {");
        sb.AppendLine("        private IntPtr _handle;");
        sb.AppendLine("        private bool _disposed;");
        sb.AppendLine("        private GCHandle _stateHandle;");
        sb.AppendLine();
        sb.AppendLine($"        internal {info.ViewName}Session(IntPtr handle) => _handle = handle;");
        sb.AppendLine();
        sb.AppendLine("        public IntPtr Handle => !_disposed");
        sb.AppendLine($"            ? _handle");
        sb.AppendLine($"            : throw new ObjectDisposedException(nameof({info.ViewName}Session));");
        sb.AppendLine();
        sb.AppendLine("        public IntPtr GetViewController() =>");
        sb.AppendLine($"            {info.ViewName}BridgeNativeMethods.GetViewController(Handle);");
        sb.AppendLine();
        EmitTypedViewControllerAccessor(sb);

        // CreateState inner class
        sb.AppendLine($"        private sealed class CreateState");
        sb.AppendLine("        {");
        sb.AppendLine($"            public TaskCompletionSource<{info.ViewName}Session> Tcs {{ get; }}");
        if (pattern.ResultCallback != null)
        {
            sb.AppendLine("            public Action<int>? OnResult { get; }");
            sb.AppendLine($"            public CreateState(TaskCompletionSource<{info.ViewName}Session> tcs, Action<int>? onResult)");
            sb.AppendLine("            { Tcs = tcs; OnResult = onResult; }");
        }
        else
        {
            sb.AppendLine($"            public CreateState(TaskCompletionSource<{info.ViewName}Session> tcs)");
            sb.AppendLine("            { Tcs = tcs; }");
        }
        sb.AppendLine("        }");
        sb.AppendLine();

        // OnReady trampoline
        sb.AppendLine($"        [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(global::System.Runtime.CompilerServices.CallConvCdecl) }})]");
        sb.AppendLine("        private static void OnReadyTrampoline(IntPtr handle, IntPtr userData)");
        sb.AppendLine("        {");
        OpenUcoFailFastGuard(sb);
        sb.AppendLine("            var stateHandle = GCHandle.FromIntPtr(userData);");
        sb.AppendLine("            var state = (CreateState)stateHandle.Target!;");
        sb.AppendLine($"            var session = new {info.ViewName}Session(handle);");
        sb.AppendLine("            session._stateHandle = stateHandle;");
        sb.AppendLine("            state.Tcs.TrySetResult(session);");
        CloseUcoFailFastGuard(sb);
        sb.AppendLine("        }");
        sb.AppendLine();

        // OnError trampoline (idempotent — safe if called twice)
        sb.AppendLine($"        [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(global::System.Runtime.CompilerServices.CallConvCdecl) }})]");
        sb.AppendLine("        private static void OnErrorTrampoline(IntPtr msgPtr, nint msgLen, IntPtr userData)");
        sb.AppendLine("        {");
        OpenUcoFailFastGuard(sb);
        sb.AppendLine("            if (userData == IntPtr.Zero) return;");
        sb.AppendLine("            var stateHandle = GCHandle.FromIntPtr(userData);");
        sb.AppendLine("            if (!stateHandle.IsAllocated) return;");
        sb.AppendLine("            var state = (CreateState)stateHandle.Target!;");
        sb.AppendLine("            stateHandle.Free();");
        sb.AppendLine("            string msg = \"(unknown error)\";");
        sb.AppendLine("            if (msgPtr != IntPtr.Zero && msgLen > 0)");
        sb.AppendLine("            {");
        sb.AppendLine("                var bytes = new byte[(int)msgLen];");
        sb.AppendLine("                Marshal.Copy(msgPtr, bytes, 0, (int)msgLen);");
        sb.AppendLine("                msg = Encoding.UTF8.GetString(bytes);");
        sb.AppendLine("            }");
        sb.AppendLine("            state.Tcs.TrySetException(new InvalidOperationException(msg));");
        CloseUcoFailFastGuard(sb);
        sb.AppendLine("        }");
        sb.AppendLine();

        // OnResult trampoline (if applicable)
        if (pattern.ResultCallback != null)
        {
            sb.AppendLine($"        [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(global::System.Runtime.CompilerServices.CallConvCdecl) }})]");
            sb.AppendLine("        private static void OnResultTrampoline(int resultCode, IntPtr userData)");
            sb.AppendLine("        {");
            OpenUcoFailFastGuard(sb);
            sb.AppendLine("            if (userData == IntPtr.Zero) return;");
            sb.AppendLine("            var stateHandle = GCHandle.FromIntPtr(userData);");
            sb.AppendLine("            if (stateHandle.IsAllocated && stateHandle.Target is CreateState state)");
            sb.AppendLine("                state.OnResult?.Invoke(resultCode);");
            CloseUcoFailFastGuard(sb);
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        EmitDataDrivenCreateAsyncFactory(sb, info, pattern);

        // Dispose pattern with finalizer: GC-driven cleanup is the only fallback when a
        // SwiftUI consumer constructs a session and stores it on a struct View without
        // a using/explicit-Dispose handshake. Without the finalizer, both the +1-retained
        // native session pointer and the pinned state GCHandle leak permanently.
        sb.AppendLine("        public void Dispose()");
        sb.AppendLine("        {");
        sb.AppendLine("            Dispose(disposing: true);");
        sb.AppendLine("            GC.SuppressFinalize(this);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        ~{info.ViewName}Session() => Dispose(disposing: false);");
        sb.AppendLine();
        sb.AppendLine("        private unsafe void Dispose(bool disposing)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (_disposed) return;");
        sb.AppendLine("            _disposed = true;");
        sb.AppendLine();
        sb.AppendLine("            // Transfer GCHandle disposal to Swift: pack the state handle (if any) into");
        sb.AppendLine("            // a native buffer and let the Swift _Free wrapper invoke our post-release");
        sb.AppendLine("            // trampoline strictly AFTER Unmanaged.release runs. This closes the");
        sb.AppendLine("            // use-after-free window where queued main-thread work (e.g. a result-monitor");
        sb.AppendLine("            // task) could dereference a freed GCHandle.");
        sb.AppendLine("            IntPtr handleBuffer = IntPtr.Zero;");
        sb.AppendLine("            int handleCount = 0;");
        sb.AppendLine("            IntPtr postReleaseFreeFn = IntPtr.Zero;");
        sb.AppendLine("            if (_stateHandle.IsAllocated)");
        sb.AppendLine("            {");
        sb.AppendLine("                handleBuffer = (IntPtr)NativeMemory.Alloc((nuint)sizeof(IntPtr));");
        sb.AppendLine("                ((IntPtr*)handleBuffer)[0] = GCHandle.ToIntPtr(_stateHandle);");
        sb.AppendLine("                handleCount = 1;");
        sb.AppendLine("                _stateHandle = default;");
        sb.AppendLine("                delegate* unmanaged[Cdecl]<IntPtr, int, void> fn = &SwiftUIBridgePostReleaseHelpers.FreeGCHandles;");
        sb.AppendLine("                postReleaseFreeFn = (IntPtr)fn;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine($"            {info.ViewName}BridgeNativeMethods.Free(_handle, handleBuffer, handleCount, postReleaseFreeFn);");
        sb.AppendLine("            _handle = IntPtr.Zero;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    /// <summary>
    /// Data-driven C# CreateAsync factory emission.
    /// Parameters and P/Invoke call are derived from the construction chain model.
    /// </summary>
    private static void EmitDataDrivenCreateAsyncFactory(
        StringBuilder sb, ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        // Factory parameter list (idiomatic C# types)
        // C# requires optional parameters after all required parameters.
        var requiredParams = new List<string>();
        var optionalParams = new List<string>();
        foreach (var param in pattern.FlattenedParams)
        {
            var type = param.Kind switch
            {
                AsyncFlatParamKind.String => "string",
                AsyncFlatParamKind.Bool => "bool",
                AsyncFlatParamKind.BoundEnum => param.CSharpTypeName ?? param.CSharpPInvokeType,
                _ => param.CSharpPInvokeType,
            };
            var defaultVal = param.Kind switch
            {
                AsyncFlatParamKind.Bool => " = true",
                _ => "",
            };
            if (defaultVal.Length > 0)
                optionalParams.Add($"{type} {param.CSharpName}{defaultVal}");
            else
                requiredParams.Add($"{type} {param.CSharpName}");
        }
        if (pattern.ResultCallback != null)
        {
            optionalParams.Add("Action<int>? onResult = null");
        }
        requiredParams.AddRange(optionalParams);

        // CreateAsync hardcodes bookkeeping locals (tcs/state/stateHandle and the
        // readyPtr/errorPtr/resultPtr function pointers). A flattened init param projecting
        // to one of those names would shadow the local → CS0136. Escape the SYNTHETIC name through
        // a scope seeded with the factory's user-facing param identifiers. No collision → zero churn.
        var asyncFactoryUserNames = new List<string>(pattern.FlattenedParams.Select(p => p.Name));
        if (pattern.ResultCallback != null)
            asyncFactoryUserNames.Add("onResult");
        var asyncFactoryScope = new SyntheticNameScope(asyncFactoryUserNames);
        var tcsLocal = asyncFactoryScope.Reserve("tcs");
        var stateLocal = asyncFactoryScope.Reserve("state");
        var stateHandleLocal = asyncFactoryScope.Reserve("stateHandle");
        var readyPtrLocal = asyncFactoryScope.Reserve("readyPtr");
        var errorPtrLocal = asyncFactoryScope.Reserve("errorPtr");
        var resultPtrLocal = asyncFactoryScope.Reserve("resultPtr");

        sb.AppendLine($"        public static async Task<{info.ViewName}Session> CreateAsync({string.Join(", ", requiredParams)})");
        sb.AppendLine("        {");

        // Validate BoundType parameters are non-zero before entering native call
        var boundTypeFactoryParams = pattern.FlattenedParams.Where(p => p.Kind is AsyncFlatParamKind.BoundType or AsyncFlatParamKind.BoundStruct).ToList();
        foreach (var param in boundTypeFactoryParams)
        {
            sb.AppendLine($"            if ({param.CSharpName} == IntPtr.Zero)");
            sb.AppendLine($"                throw new ArgumentNullException(nameof({param.CSharpName}));");
        }
        if (boundTypeFactoryParams.Count > 0)
            sb.AppendLine();

        sb.AppendLine($"            var {tcsLocal} = new TaskCompletionSource<{info.ViewName}Session>(");
        sb.AppendLine("                TaskCreationOptions.RunContinuationsAsynchronously);");
        if (pattern.ResultCallback != null)
        {
            sb.AppendLine($"            var {stateLocal} = new CreateState({tcsLocal}, onResult);");
        }
        else
        {
            sb.AppendLine($"            var {stateLocal} = new CreateState({tcsLocal});");
        }
        sb.AppendLine($"            var {stateHandleLocal} = GCHandle.Alloc({stateLocal});");
        sb.AppendLine();
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                unsafe");
        sb.AppendLine("                {");
        sb.AppendLine($"                    delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> {readyPtrLocal} = &OnReadyTrampoline;");
        sb.AppendLine($"                    delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> {errorPtrLocal} = &OnErrorTrampoline;");
        if (pattern.ResultCallback != null)
        {
            sb.AppendLine($"                    delegate* unmanaged[Cdecl]<int, IntPtr, void> {resultPtrLocal} = &OnResultTrampoline;");
        }
        sb.AppendLine();

        // String encoding
        var stringParams = pattern.FlattenedParams.Where(p => p.Kind == AsyncFlatParamKind.String).ToList();
        foreach (var param in stringParams)
        {
            sb.AppendLine($"                    var {param.Name}Bytes = Encoding.UTF8.GetBytes({param.CSharpName} ?? \"\");");
        }

        // Fixed block for strings (if any)
        if (stringParams.Count > 0)
        {
            var fixedDecls = string.Join(", ", stringParams.Select(p => $"byte* {p.Name}Ptr = {p.Name}Bytes"));
            sb.AppendLine($"                    fixed ({fixedDecls})");
            sb.AppendLine("                    {");
            EmitDataDrivenCreateAsyncCall(sb, info, pattern, "                        ", readyPtrLocal, errorPtrLocal, resultPtrLocal, stateHandleLocal);
            sb.AppendLine("                    }");
        }
        else
        {
            EmitDataDrivenCreateAsyncCall(sb, info, pattern, "                    ", readyPtrLocal, errorPtrLocal, resultPtrLocal, stateHandleLocal);
        }

        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            catch");
        sb.AppendLine("            {");
        sb.AppendLine($"                if ({stateHandleLocal}.IsAllocated) {stateHandleLocal}.Free();");
        sb.AppendLine("                throw;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine($"            return await {tcsLocal}.Task;");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void EmitDataDrivenCreateAsyncCall(
        StringBuilder sb, ViewBridgeInfo info, AsyncViewPattern pattern, string indent,
        string readyPtrLocal, string errorPtrLocal, string resultPtrLocal, string stateHandleLocal)
    {
        var nativeArgs = new List<string>();
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.String)
            {
                nativeArgs.Add($"(IntPtr){param.Name}Ptr");
                nativeArgs.Add($"{param.Name}Bytes.Length");
            }
            else if (param.Kind == AsyncFlatParamKind.Bool)
            {
                nativeArgs.Add($"{param.CSharpName} ? 1 : 0");
            }
            else if (param.Kind == AsyncFlatParamKind.BoundEnum)
            {
                // Simple enums are C# enum value types — cast to the underlying int.
                // Complex enums are C# classes exposing a .RawValue property; casting the
                // class to int is CS0030 and breaks the generated binding's compile.
                nativeArgs.Add(param.IsSimpleEnum
                    ? $"({param.CSharpPInvokeType}){param.CSharpName}"
                    : $"{param.CSharpName}.RawValue");
            }
            else
            {
                nativeArgs.Add(param.CSharpName);
            }
        }
        nativeArgs.Add($"(IntPtr){readyPtrLocal}");
        nativeArgs.Add($"(IntPtr){errorPtrLocal}");
        if (pattern.ResultCallback != null)
        {
            nativeArgs.Add($"(IntPtr){resultPtrLocal}");
        }
        nativeArgs.Add($"GCHandle.ToIntPtr({stateHandleLocal})");

        sb.AppendLine($"{indent}{info.ViewName}BridgeNativeMethods.Create(");
        for (int i = 0; i < nativeArgs.Count; i++)
        {
            var comma = i < nativeArgs.Count - 1 ? "," : ");";
            sb.AppendLine($"{indent}    {nativeArgs[i]}{comma}");
        }
    }

    #endregion
}

/// <summary>
/// Configuration for a data-driven async View bridge pattern. The construction chain
/// describes the sequence of Swift objects created inside the async Create function;
/// <see cref="SessionFields"/> picks which of those are retained on the session class.
/// </summary>
public record AsyncViewPattern(
    string ViewName,
    string SessionClassName,
    string[] ExtraSwiftImports,
    AsyncSessionField[] SessionFields,
    AsyncFlatParam[] FlattenedParams,
    List<AsyncConstructionStep> ConstructionChain,
    AsyncResultCallbackConfig? ResultCallback = null,
    IReadOnlyList<ConstructionArg>? ViewInitArgs = null);

/// <summary>
/// Describes the result-callback machinery for async Views. When non-null on an
/// <see cref="AsyncViewPattern"/>, the generator emits the ResultFn typedef,
/// resultTask field, startResultMonitor / cancelResultMonitor helpers, plus the
/// matching C# OnResultTrampoline, CreateState(Action&lt;int&gt;?), and resultPtr wiring.
/// </summary>
public record AsyncResultCallbackConfig(
    string SourceFieldName,
    string AwaitMethodName,
    IReadOnlyList<(string SwiftCase, int Code)> ResultCases);

/// <summary>
/// A single step in an async construction chain.
/// Each step represents creating one intermediate object that the View depends on.
/// When <paramref name="FactoryMethod"/> is non-null, the step calls
/// <c>SwiftTypeName.FactoryMethod(args)</c> instead of <c>SwiftTypeName(args)</c>.
/// </summary>
public record AsyncConstructionStep(
    string VariableName,
    string SwiftTypeName,
    bool IsAsync,
    bool Throws,
    List<ConstructionArg> Args,
    string? FactoryMethod = null)
{
    /// <summary><see cref="VariableName"/> escaped as a BARE Swift identifier (backtick-wrapped for a
    /// Swift keyword). Use at the chain-step local declaration and any reference to it.</summary>
    public string SwiftName => NameProvider.EscapeSwiftKeyword(VariableName);
}

/// <summary>
/// An argument to a construction step. <c>ParamLabel</c> is the original Swift argument
/// label, emitted bare at the call site (a Swift keyword label needs no backticks there).
/// </summary>
public record ConstructionArg(
    string ParamLabel,
    ConstructionArgKind Kind,
    string Value);

/// <summary>
/// Kind of construction argument in an async chain step.
/// </summary>
public enum ConstructionArgKind
{
    /// <summary>Leaf value from C# (flattened parameter in Create()).</summary>
    FlattenedParam,
    /// <summary>Reference to a previous step's output variable.</summary>
    ChainReference,
    /// <summary>Field access on a previous step's output (step.fieldName).</summary>
    FieldAccess,
    /// <summary>A literal value.</summary>
    Literal,
}

/// <summary>
/// A field in the async session class.
/// </summary>
public record AsyncSessionField(string Name, string SwiftType)
{
    /// <summary><see cref="Name"/> escaped as a BARE Swift identifier (backtick-wrapped for a Swift
    /// keyword). Use at the stored-property declaration, init label/param, and ctor-call sites.</summary>
    public string SwiftName => NameProvider.EscapeSwiftKeyword(Name);
}

/// <summary>
/// A flattened parameter for the async Create function.
/// </summary>
public record AsyncFlatParam(
    string Name,
    AsyncFlatParamKind Kind,
    string SwiftAbiType,
    string CSharpPInvokeType,
    string? SwiftConversion,
    string? CSharpConversion,
    string? BridgeTypeName = null,
    string? CSharpTypeName = null,
    string? SourceModule = null,
    bool IsObjCBridgeable = false,
    bool IsSimpleEnum = false)
{
    /// <summary><see cref="Name"/> escaped as a BARE C# identifier (`@`-prefixed for a C# keyword).
    /// Use at bare C# identifier sites; compound names like {Name}Ptr stay raw.</summary>
    public string CSharpName => NameProvider.EscapeForCSharpSignature(Name);

    /// <summary><see cref="Name"/> escaped as a BARE Swift identifier (backtick-wrapped for a Swift
    /// keyword). Use at bare Swift identifier sites incl. argument labels; compounds stay raw, and
    /// never inside @_cdecl symbol-name string literals.</summary>
    public string SwiftName => NameProvider.EscapeSwiftKeyword(Name);
}

/// <summary>
/// Kind of flattened async parameter.
/// </summary>
public enum AsyncFlatParamKind
{
    String,
    Bool,
    Primitive,
    BoundType,
    BoundStruct,
    BoundEnum,
}
