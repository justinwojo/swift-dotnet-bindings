// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Async-specific bridge generation for SwiftUI Views with async dependency chains.
/// Phase 4: Explicit support for BlinkIDUXView pattern only.
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
            SessionClassName: "BlinkIDUXSession",
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
            HasResultCallback: true),
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

        return new AsyncViewPattern(
            ViewName: view.ViewName,
            SessionClassName: $"SBW_{view.ModuleName}_{view.ViewName}_Session",
            ExtraSwiftImports: Array.Empty<string>(),
            SessionFields: chain.Select(s => new AsyncSessionField(s.VariableName, s.SwiftTypeName)).ToArray(),
            FlattenedParams: flatParams.ToArray(),
            HasResultCallback: false,
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
                        if (isModuleType)
                        {
                            args.Add(new ConstructionArg(innerParam.Name, ConstructionArgKind.ChainReference, ToVariableName(innerParam)));
                        }
                        else
                        {
                            args.Add(new ConstructionArg(innerParam.Name, ConstructionArgKind.FlattenedParam, innerParam.Name));
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

            // Not a same-module type — try leaf mapping (primitives, String, Bool, closures)
            var bridgeParam = MapParameterType(param, context);
            if (bridgeParam != null)
            {
                var flatParam = BridgeParamToFlatParam(bridgeParam);
                if (flatParam == null)
                    return false; // unsupported leaf kind for async flattening
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
            _ => null, // Closures, enums, etc. not supported in async flattening yet
        };
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
        StringBuilder sb, string moduleName, ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        var prefix = $"SBW_{moduleName}_{info.ViewName}";
        // Data-driven uses pattern's session name; legacy uses prefix-based naming for backward compat
        var sessionClass = pattern.ConstructionChain != null ? pattern.SessionClassName : $"{prefix}_Session";
        var handlesVar = $"{prefix}_liveHandles";

        sb.AppendLine($"// --- {info.ViewName} (Async) ---");
        sb.AppendLine();

        // Callback typedefs
        sb.AppendLine($"/// C function pointer: (handle, userData) → called on success.");
        sb.AppendLine($"public typealias {prefix}_ReadyFn = @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer?) -> Void");
        sb.AppendLine($"/// C function pointer: (msgPtr, msgLen, userData) → called on error.");
        sb.AppendLine($"public typealias {prefix}_ErrorFn = @convention(c) (UnsafePointer<UInt8>, Int, UnsafeMutableRawPointer?) -> Void");
        if (pattern.HasResultCallback)
        {
            sb.AppendLine($"/// C function pointer: (resultCode, userData) → called when operation completes.");
            sb.AppendLine($"public typealias {prefix}_ResultFn = @convention(c) (Int32, UnsafeMutableRawPointer?) -> Void");
        }
        sb.AppendLine();

        // Session class — data-driven vs legacy
        if (pattern.ConstructionChain != null)
        {
            EmitDataDrivenSessionClass(sb, prefix, sessionClass, handlesVar, info, pattern);
        }
        else
        {
            EmitLegacySessionClass(sb, prefix, sessionClass, handlesVar, info, pattern);
        }

        // Handle tracking
        sb.AppendLine($"var {handlesVar} = Set<UnsafeMutableRawPointer>()");
        sb.AppendLine();

        // Create function (async factory)
        EmitAsyncCreateFunction(sb, prefix, sessionClass, handlesVar, moduleName, info, pattern);

        // GetViewController function
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

        // Free function
        sb.AppendLine($"@_cdecl(\"{prefix}_Free\")");
        sb.AppendLine($"public func {prefix}_Free(_ handle: UnsafeMutableRawPointer?) {{");
        sb.AppendLine("    SBW_onMainThread {");
        sb.AppendLine($"        guard let handle = handle,");
        sb.AppendLine($"              {handlesVar}.remove(handle) != nil else {{ return }}");
        if (pattern.HasResultCallback)
        {
            sb.AppendLine($"        let session = Unmanaged<{sessionClass}>.fromOpaque(handle).takeUnretainedValue()");
            sb.AppendLine($"        session.cancelResultMonitor()");
        }
        sb.AppendLine($"        Unmanaged<{sessionClass}>.fromOpaque(handle).release()");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>
    /// Emits the Session class for data-driven async views.
    /// The session receives a pre-built UIHostingController from the Create function
    /// (where both chain outputs and flattened leaf params are in scope) and stores
    /// chain step outputs as fields for ARC retention.
    /// </summary>
    private static void EmitDataDrivenSessionClass(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        var chain = pattern.ConstructionChain!;

        sb.AppendLine($"final class {sessionClass} {{");
        foreach (var field in pattern.SessionFields)
        {
            sb.AppendLine($"    let {field.Name}: {field.SwiftType}");
        }
        sb.AppendLine($"    let hostingController: UIHostingController<{info.ViewName}>");
        sb.AppendLine();

        // Session init takes chain step outputs (for retention) + pre-built hosting controller
        sb.AppendLine($"    @MainActor");
        sb.Append($"    init(");
        var initParams = chain.Select(s => $"{s.VariableName}: {s.SwiftTypeName}").ToList();
        initParams.Add($"hostingController: UIHostingController<{info.ViewName}>");
        sb.Append(string.Join(",\n         ", initParams));
        sb.AppendLine(") {");

        // Store chain step outputs for ARC retention
        foreach (var step in chain)
        {
            sb.AppendLine($"        self.{step.VariableName} = {step.VariableName}");
        }
        sb.AppendLine($"        self.hostingController = hostingController");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>
    /// Builds the View init argument list from the chain steps and flattened params.
    /// Maps the View's constructor parameter labels to either chain step variables
    /// or flattened leaf param variables (with Bool/String conversions applied).
    /// Called in the Create function scope where both chain outputs and leaf params exist.
    /// </summary>
    private static List<string> BuildViewInitArgsFromChain(ViewBridgeInfo info, List<AsyncConstructionStep> chain,
        AsyncFlatParam[]? flatParams = null)
    {
        var args = new List<string>();
        if (info.Constructors.Count == 0)
            return args;

        var ctor = info.Constructors[0];
        // CSSignature[0] is return type; params start at index 1
        for (int i = 1; i < ctor.CSSignature.Count; i++)
        {
            var param = ctor.CSSignature[i];
            // Find matching chain step by variable name
            var step = chain.FirstOrDefault(s => s.VariableName == ToVariableName(param));
            if (step != null)
            {
                args.Add($"{param.Name}: {step.VariableName}");
            }
            else
            {
                // Leaf param — apply Bool/String conversion if flattened params are available
                var varName = ToVariableName(param);
                if (flatParams != null)
                    varName = FormatFlatParamSwiftValue(varName, flatParams);
                args.Add($"{param.Name}: {varName}");
            }
        }
        return args;
    }

    /// <summary>
    /// Legacy hard-coded Session class emission (BlinkIDUX-specific).
    /// Used when ConstructionChain is null (dictionary-based patterns).
    /// </summary>
    private static void EmitLegacySessionClass(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        sb.AppendLine($"final class {sessionClass} {{");
        foreach (var field in pattern.SessionFields)
        {
            sb.AppendLine($"    let {field.Name}: {field.SwiftType}");
        }
        sb.AppendLine($"    let hostingController: UIHostingController<{info.ViewName}>");
        if (pattern.HasResultCallback)
        {
            sb.AppendLine($"    private var resultTask: Task<Void, Never>?");
        }
        sb.AppendLine();

        // Session init
        sb.AppendLine($"    @MainActor");
        sb.Append($"    init(");
        var initParams = pattern.SessionFields.Select(f => $"{f.Name}: {f.SwiftType}");
        sb.Append(string.Join(",\n         ", initParams));
        sb.AppendLine(") {");
        foreach (var field in pattern.SessionFields)
        {
            sb.AppendLine($"        self.{field.Name} = {field.Name}");
        }
        sb.AppendLine();
        sb.AppendLine($"        let view = {info.ViewName}(viewModel: model)");
        sb.AppendLine($"        self.hostingController = UIHostingController(rootView: view)");
        sb.AppendLine("    }");

        // Result monitor (if applicable)
        if (pattern.HasResultCallback)
        {
            sb.AppendLine();
            EmitResultMonitor(sb, prefix, sessionClass, handlesVar);
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitResultMonitor(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar)
    {
        sb.AppendLine($"    @MainActor");
        sb.AppendLine($"    func startResultMonitor(handle: UnsafeMutableRawPointer,");
        sb.AppendLine($"                            resultCallback: {prefix}_ResultFn?,");
        sb.AppendLine($"                            userData: UnsafeMutableRawPointer?) {{");
        sb.AppendLine($"        let analyzerRef = analyzer");
        sb.AppendLine($"        let cb = resultCallback");
        sb.AppendLine($"        let ud = userData");
        sb.AppendLine($"        let sessionHandle = handle");
        sb.AppendLine($"        self.resultTask = Task {{ @MainActor in");
        sb.AppendLine($"            let result = await analyzerRef.result()");
        sb.AppendLine($"            guard !Task.isCancelled else {{ return }}");
        sb.AppendLine($"            guard {handlesVar}.contains(sessionHandle) else {{ return }}");
        sb.AppendLine($"            let code: Int32");
        sb.AppendLine($"            switch result {{");
        sb.AppendLine($"            case .completed: code = 0");
        sb.AppendLine($"            case .interrupted: code = 1");
        sb.AppendLine($"            case .cancelled: code = 2");
        sb.AppendLine($"            case .ended: code = 3");
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

    private static void EmitAsyncCreateFunction(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        string moduleName, ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        if (pattern.ConstructionChain != null)
        {
            EmitDataDrivenAsyncCreate(sb, prefix, sessionClass, handlesVar, moduleName, info, pattern);
        }
        else
        {
            EmitLegacyAsyncCreate(sb, prefix, sessionClass, handlesVar, moduleName, info, pattern);
        }
    }

    /// <summary>
    /// Data-driven async Create function emission (Phase 2B).
    /// Iterates the ConstructionChain steps to emit the Swift @_cdecl factory.
    /// </summary>
    private static void EmitDataDrivenAsyncCreate(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        string moduleName, ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        var chain = pattern.ConstructionChain!;

        // @_cdecl signature with flattened params + callbacks
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
            else
            {
                createParams.Add($"_ {param.Name}: {param.SwiftAbiType}");
            }
        }
        createParams.Add($"_ onReady: {prefix}_ReadyFn?");
        createParams.Add($"_ onError: {prefix}_ErrorFn?");
        createParams.Add("_ userData: UnsafeMutableRawPointer?");

        sb.AppendLine(string.Join(",\n    ", createParams));
        sb.AppendLine(") {");

        // Guard onReady
        sb.AppendLine("    guard let onReady = onReady else { return }");
        sb.AppendLine();

        // Copy string parameters eagerly (before Task)
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.String)
            {
                sb.AppendLine($"    let {param.Name}: String");
                sb.AppendLine($"    if let ptr = {param.Name}Ptr, {param.Name}Len > 0 {{");
                sb.AppendLine($"        {param.Name} = String(");
                sb.AppendLine($"            bytes: UnsafeBufferPointer(start: ptr, count: {param.Name}Len),");
                sb.AppendLine($"            encoding: .utf8");
                sb.AppendLine($"        ) ?? \"\"");
                sb.AppendLine($"    }} else {{");
                sb.AppendLine($"        {param.Name} = \"\"");
                sb.AppendLine($"    }}");
                sb.AppendLine();
            }
        }

        // Bool conversions eagerly (before Task)
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.Bool)
            {
                sb.AppendLine($"    let {param.Name}Val: Bool = {param.Name} != 0");
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
                    ConstructionArgKind.ChainReference => arg.Value,
                    ConstructionArgKind.FlattenedParam => FormatFlatParamSwiftValue(arg.Value, pattern.FlattenedParams),
                    ConstructionArgKind.FieldAccess => arg.Value,
                    ConstructionArgKind.Literal => arg.Value,
                    _ => arg.Value,
                };
                args.Add($"{arg.ParamLabel}: {value}");
            }

            var argStr = args.Count > 0 ? string.Join(", ", args) : "";
            sb.AppendLine($"{indent}let {step.VariableName} = {tryAwait}{step.SwiftTypeName}({argStr})");
        }

        sb.AppendLine();

        // Build the View here in Create scope where both chain outputs and
        // flattened leaf params are available (fixes mixed chain + leaf param views)
        var viewInitArgs = BuildViewInitArgsFromChain(info, chain, pattern.FlattenedParams);
        if (viewInitArgs.Count == 0)
            sb.AppendLine($"{indent}let rootView = {info.ViewName}()");
        else
            sb.AppendLine($"{indent}let rootView = {info.ViewName}({string.Join(", ", viewInitArgs)})");
        sb.AppendLine($"{indent}let hc = UIHostingController(rootView: rootView)");
        sb.AppendLine();

        // Build session — pass chain step outputs (for retention) + pre-built hosting controller
        sb.AppendLine($"{indent}let session = {sessionClass}(");
        var sessionArgs = chain.Select(s => $"{indent}    {s.VariableName}: {s.VariableName}").ToList();
        sessionArgs.Add($"{indent}    hostingController: hc");
        sb.AppendLine(string.Join(",\n", sessionArgs));
        sb.AppendLine($"{indent})");

        sb.AppendLine($"{indent}let handle = Unmanaged.passRetained(session).toOpaque()");
        sb.AppendLine($"{indent}{handlesVar}.insert(handle)");
        sb.AppendLine();
        sb.AppendLine($"{indent}onReady(handle, userData)");

        if (hasThrows)
        {
            sb.AppendLine("        } catch {");
            sb.AppendLine("            if let onError = onError {");
            sb.AppendLine("                let msg = \"\\(error)\"");
            sb.AppendLine("                let utf8 = Array(msg.utf8)");
            sb.AppendLine("                utf8.withUnsafeBufferPointer { buf in");
            sb.AppendLine("                    guard let base = buf.baseAddress else { return }");
            sb.AppendLine("                    onError(base, buf.count, userData)");
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
        return paramName;
    }

    /// <summary>
    /// Legacy hard-coded async Create function emission (BlinkIDUX-specific).
    /// Used when ConstructionChain is null (dictionary-based patterns).
    /// </summary>
    private static void EmitLegacyAsyncCreate(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        string moduleName, ViewBridgeInfo info, AsyncViewPattern pattern)
    {
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
            else
            {
                createParams.Add($"_ {param.Name}: {param.SwiftAbiType}");
            }
        }
        createParams.Add($"_ onReady: {prefix}_ReadyFn?");
        createParams.Add($"_ onError: {prefix}_ErrorFn?");
        if (pattern.HasResultCallback)
        {
            createParams.Add($"_ onResult: {prefix}_ResultFn?");
        }
        createParams.Add("_ userData: UnsafeMutableRawPointer?");

        sb.AppendLine(string.Join(",\n    ", createParams));
        sb.AppendLine(") {");

        // Guard onReady
        sb.AppendLine("    guard let onReady = onReady else { return }");
        sb.AppendLine();

        // Copy string parameters
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.String)
            {
                sb.AppendLine($"    let {param.Name}: String");
                sb.AppendLine($"    if let ptr = {param.Name}Ptr, {param.Name}Len > 0 {{");
                sb.AppendLine($"        {param.Name} = String(");
                sb.AppendLine($"            bytes: UnsafeBufferPointer(start: ptr, count: {param.Name}Len),");
                sb.AppendLine($"            encoding: .utf8");
                sb.AppendLine($"        ) ?? \"\"");
                sb.AppendLine($"    }} else {{");
                sb.AppendLine($"        {param.Name} = \"\"");
                sb.AppendLine($"    }}");
                sb.AppendLine();
            }
        }

        // Build UX settings
        sb.AppendLine("    let uxSettings = ScanningUXSettings(");
        sb.AppendLine("        showIntroductionAlert: showIntroductionAlert != 0,");
        sb.AppendLine("        showHelpButton: showHelpButton != 0,");
        sb.AppendLine("        preferredCameraPosition: preferFrontCamera != 0 ? .front : .back,");
        sb.AppendLine("        allowHapticFeedback: allowHapticFeedback != 0");
        sb.AppendLine("    )");
        sb.AppendLine();

        // Async Task
        sb.AppendLine("    Task { @MainActor in");
        sb.AppendLine("        do {");
        sb.AppendLine("            let sdkSettings = BlinkIDSdkSettings(licenseKey: licenseKey)");
        sb.AppendLine("            let sdk = try await BlinkIDSdk.createBlinkIDSdk(withSettings: sdkSettings)");
        sb.AppendLine("            let eventStream = BlinkIDEventStream()");
        sb.AppendLine("            let analyzer = try await BlinkIDAnalyzer(");
        sb.AppendLine("                sdk: sdk,");
        sb.AppendLine("                eventStream: eventStream");
        sb.AppendLine("            )");
        sb.AppendLine("            let model = BlinkIDUXModel(");
        sb.AppendLine("                analyzer: analyzer,");
        sb.AppendLine("                uxSettings: uxSettings,");
        sb.AppendLine("                sessionNumber: analyzer.sessionNumber");
        sb.AppendLine("            )");
        sb.AppendLine();
        sb.AppendLine($"            let session = {sessionClass}(");
        sb.AppendLine("                sdk: sdk,");
        sb.AppendLine("                eventStream: eventStream,");
        sb.AppendLine("                analyzer: analyzer,");
        sb.AppendLine("                model: model");
        sb.AppendLine("            )");
        sb.AppendLine($"            let handle = Unmanaged.passRetained(session).toOpaque()");
        sb.AppendLine($"            {handlesVar}.insert(handle)");

        if (pattern.HasResultCallback)
        {
            sb.AppendLine("            session.startResultMonitor(");
            sb.AppendLine("                handle: handle,");
            sb.AppendLine("                resultCallback: onResult,");
            sb.AppendLine("                userData: userData");
            sb.AppendLine("            )");
        }

        sb.AppendLine();
        sb.AppendLine("            onReady(handle, userData)");
        sb.AppendLine("        } catch {");
        sb.AppendLine("            if let onError = onError {");
        sb.AppendLine("                let msg = \"\\(error)\"");
        sb.AppendLine("                let utf8 = Array(msg.utf8)");
        sb.AppendLine("                utf8.withUnsafeBufferPointer { buf in");
        sb.AppendLine("                    guard let base = buf.baseAddress else { return }");
        sb.AppendLine("                    onError(base, buf.count, userData)");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    #endregion

    #region Async C# Generation

    internal static void EmitAsyncCSharpBridge(
        StringBuilder sb, string moduleName, ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        var prefix = $"SBW_{moduleName}_{info.ViewName}";
        var bridgeLib = $"{moduleName}Bridge";

        // NativeMethods class
        sb.AppendLine($"    internal static class {info.ViewName}BridgeNativeMethods");
        sb.AppendLine("    {");
        sb.AppendLine($"        private const string BridgeLib = \"{bridgeLib}\";");
        sb.AppendLine();

        // Create P/Invoke (returns void — async factory)
        sb.AppendLine($"        [DllImport(BridgeLib, EntryPoint = \"{prefix}_Create\")]");
        sb.AppendLine($"        [UnmanagedCallConv(CallConvs = new[] {{ typeof(CallConvCdecl) }})]");

        var createPInvokeParams = new List<string>();
        foreach (var param in pattern.FlattenedParams)
        {
            if (param.Kind == AsyncFlatParamKind.String)
            {
                createPInvokeParams.Add($"IntPtr {param.Name}Ptr");
                createPInvokeParams.Add($"nint {param.Name}Len");
            }
            else
            {
                createPInvokeParams.Add($"{param.CSharpPInvokeType} {param.Name}");
            }
        }
        createPInvokeParams.Add("IntPtr onReady");
        createPInvokeParams.Add("IntPtr onError");
        if (pattern.HasResultCallback)
        {
            createPInvokeParams.Add("IntPtr onResult");
        }
        createPInvokeParams.Add("IntPtr userData");

        sb.AppendLine($"        internal static extern void Create({string.Join(", ", createPInvokeParams)});");
        sb.AppendLine();

        // GetViewController P/Invoke
        sb.AppendLine($"        [DllImport(BridgeLib, EntryPoint = \"{prefix}_GetViewController\")]");
        sb.AppendLine($"        [UnmanagedCallConv(CallConvs = new[] {{ typeof(CallConvCdecl) }})]");
        sb.AppendLine($"        internal static extern IntPtr GetViewController(IntPtr handle);");
        sb.AppendLine();

        // Free P/Invoke
        sb.AppendLine($"        [DllImport(BridgeLib, EntryPoint = \"{prefix}_Free\")]");
        sb.AppendLine($"        [UnmanagedCallConv(CallConvs = new[] {{ typeof(CallConvCdecl) }})]");
        sb.AppendLine($"        internal static extern void Free(IntPtr handle);");
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

        // CreateState inner class
        sb.AppendLine($"        private sealed class CreateState");
        sb.AppendLine("        {");
        sb.AppendLine($"            public TaskCompletionSource<{info.ViewName}Session> Tcs {{ get; }}");
        if (pattern.HasResultCallback)
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
        sb.AppendLine($"        [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(CallConvCdecl) }})]");
        sb.AppendLine("        private static void OnReadyTrampoline(IntPtr handle, IntPtr userData)");
        sb.AppendLine("        {");
        sb.AppendLine("            var stateHandle = GCHandle.FromIntPtr(userData);");
        sb.AppendLine("            var state = (CreateState)stateHandle.Target!;");
        sb.AppendLine($"            var session = new {info.ViewName}Session(handle);");
        sb.AppendLine("            session._stateHandle = stateHandle;");
        sb.AppendLine("            state.Tcs.TrySetResult(session);");
        sb.AppendLine("        }");
        sb.AppendLine();

        // OnError trampoline (idempotent — safe if called twice)
        sb.AppendLine($"        [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(CallConvCdecl) }})]");
        sb.AppendLine("        private static void OnErrorTrampoline(IntPtr msgPtr, nint msgLen, IntPtr userData)");
        sb.AppendLine("        {");
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
        sb.AppendLine("        }");
        sb.AppendLine();

        // OnResult trampoline (if applicable)
        if (pattern.HasResultCallback)
        {
            sb.AppendLine($"        [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(CallConvCdecl) }})]");
            sb.AppendLine("        private static void OnResultTrampoline(int resultCode, IntPtr userData)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (userData == IntPtr.Zero) return;");
            sb.AppendLine("            var stateHandle = GCHandle.FromIntPtr(userData);");
            sb.AppendLine("            if (stateHandle.IsAllocated && stateHandle.Target is CreateState state)");
            sb.AppendLine("                state.OnResult?.Invoke(resultCode);");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // CreateAsync factory — data-driven vs legacy
        if (pattern.ConstructionChain != null)
        {
            EmitDataDrivenCreateAsyncFactory(sb, info, pattern);
        }
        else
        {
            EmitLegacyCreateAsyncFactory(sb, info, pattern);
        }

        sb.AppendLine("        public void Dispose()");
        sb.AppendLine("        {");
        sb.AppendLine("            if (!_disposed)");
        sb.AppendLine("            {");
        sb.AppendLine("                _disposed = true;");
        sb.AppendLine($"                {info.ViewName}BridgeNativeMethods.Free(_handle);");
        sb.AppendLine("                if (_stateHandle.IsAllocated) _stateHandle.Free();");
        sb.AppendLine("                _handle = IntPtr.Zero;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    /// <summary>
    /// Data-driven C# CreateAsync factory emission (Phase 2B).
    /// Parameters and P/Invoke call are derived from the construction chain model.
    /// </summary>
    private static void EmitDataDrivenCreateAsyncFactory(
        StringBuilder sb, ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        // Factory parameter list (idiomatic C# types)
        var factoryParams = new List<string>();
        foreach (var param in pattern.FlattenedParams)
        {
            var type = param.Kind switch
            {
                AsyncFlatParamKind.String => "string",
                AsyncFlatParamKind.Bool => "bool",
                _ => param.CSharpPInvokeType,
            };
            var defaultVal = param.Kind switch
            {
                AsyncFlatParamKind.Bool => " = true",
                _ => "",
            };
            factoryParams.Add($"{type} {param.Name}{defaultVal}");
        }

        sb.AppendLine($"        public static async Task<{info.ViewName}Session> CreateAsync({string.Join(", ", factoryParams)})");
        sb.AppendLine("        {");
        sb.AppendLine($"            var tcs = new TaskCompletionSource<{info.ViewName}Session>(");
        sb.AppendLine("                TaskCreationOptions.RunContinuationsAsynchronously);");
        sb.AppendLine("            var state = new CreateState(tcs);");
        sb.AppendLine("            var stateHandle = GCHandle.Alloc(state);");
        sb.AppendLine();
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                unsafe");
        sb.AppendLine("                {");
        sb.AppendLine("                    delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> readyPtr = &OnReadyTrampoline;");
        sb.AppendLine("                    delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> errorPtr = &OnErrorTrampoline;");
        sb.AppendLine();

        // String encoding
        var stringParams = pattern.FlattenedParams.Where(p => p.Kind == AsyncFlatParamKind.String).ToList();
        foreach (var param in stringParams)
        {
            sb.AppendLine($"                    var {param.Name}Bytes = Encoding.UTF8.GetBytes({param.Name} ?? \"\");");
        }

        // Fixed block for strings (if any)
        if (stringParams.Count > 0)
        {
            var fixedDecls = string.Join(", ", stringParams.Select(p => $"byte* {p.Name}Ptr = {p.Name}Bytes"));
            sb.AppendLine($"                    fixed ({fixedDecls})");
            sb.AppendLine("                    {");
            EmitDataDrivenCreateAsyncCall(sb, info, pattern, "                        ");
            sb.AppendLine("                    }");
        }
        else
        {
            EmitDataDrivenCreateAsyncCall(sb, info, pattern, "                    ");
        }

        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            catch");
        sb.AppendLine("            {");
        sb.AppendLine("                if (stateHandle.IsAllocated) stateHandle.Free();");
        sb.AppendLine("                throw;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            return await tcs.Task;");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void EmitDataDrivenCreateAsyncCall(
        StringBuilder sb, ViewBridgeInfo info, AsyncViewPattern pattern, string indent)
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
                nativeArgs.Add($"{param.Name} ? 1 : 0");
            }
            else
            {
                nativeArgs.Add(param.Name);
            }
        }
        nativeArgs.Add("(IntPtr)readyPtr");
        nativeArgs.Add("(IntPtr)errorPtr");
        nativeArgs.Add("GCHandle.ToIntPtr(stateHandle)");

        sb.AppendLine($"{indent}{info.ViewName}BridgeNativeMethods.Create(");
        for (int i = 0; i < nativeArgs.Count; i++)
        {
            var comma = i < nativeArgs.Count - 1 ? "," : ");";
            sb.AppendLine($"{indent}    {nativeArgs[i]}{comma}");
        }
    }

    /// <summary>
    /// Legacy hard-coded C# CreateAsync factory emission (BlinkIDUX-specific).
    /// Used when ConstructionChain is null (dictionary-based patterns).
    /// </summary>
    private static void EmitLegacyCreateAsyncFactory(
        StringBuilder sb, ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        // Factory parameter list (idiomatic C# types)
        var factoryParams = new List<string>();
        foreach (var param in pattern.FlattenedParams)
        {
            var type = param.Kind switch
            {
                AsyncFlatParamKind.String => "string",
                AsyncFlatParamKind.Bool => "bool",
                _ => param.CSharpPInvokeType,
            };
            var defaultVal = param.Kind switch
            {
                AsyncFlatParamKind.Bool => " = true",
                _ => "",
            };
            factoryParams.Add($"{type} {param.Name}{defaultVal}");
        }
        if (pattern.HasResultCallback)
        {
            factoryParams.Add("Action<int>? onResult = null");
        }

        sb.AppendLine($"        public static async Task<{info.ViewName}Session> CreateAsync({string.Join(", ", factoryParams)})");
        sb.AppendLine("        {");
        sb.AppendLine($"            var tcs = new TaskCompletionSource<{info.ViewName}Session>(");
        sb.AppendLine("                TaskCreationOptions.RunContinuationsAsynchronously);");
        if (pattern.HasResultCallback)
        {
            sb.AppendLine("            var state = new CreateState(tcs, onResult);");
        }
        else
        {
            sb.AppendLine("            var state = new CreateState(tcs);");
        }
        sb.AppendLine("            var stateHandle = GCHandle.Alloc(state);");
        sb.AppendLine();
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                unsafe");
        sb.AppendLine("                {");
        sb.AppendLine("                    delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> readyPtr = &OnReadyTrampoline;");
        sb.AppendLine("                    delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> errorPtr = &OnErrorTrampoline;");
        if (pattern.HasResultCallback)
        {
            sb.AppendLine("                    delegate* unmanaged[Cdecl]<int, IntPtr, void> resultPtr = &OnResultTrampoline;");
        }
        sb.AppendLine();

        // String encoding
        var stringParams = pattern.FlattenedParams.Where(p => p.Kind == AsyncFlatParamKind.String).ToList();
        foreach (var param in stringParams)
        {
            sb.AppendLine($"                    var {param.Name}Bytes = Encoding.UTF8.GetBytes({param.Name} ?? \"\");");
        }

        // Fixed block for strings (if any)
        if (stringParams.Count > 0)
        {
            var fixedDecls = string.Join(", ", stringParams.Select(p => $"byte* {p.Name}Ptr = {p.Name}Bytes"));
            sb.AppendLine($"                    fixed ({fixedDecls})");
            sb.AppendLine("                    {");
            EmitLegacyCreateAsyncCall(sb, info, pattern, "                        ");
            sb.AppendLine("                    }");
        }
        else
        {
            EmitLegacyCreateAsyncCall(sb, info, pattern, "                    ");
        }

        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            catch");
        sb.AppendLine("            {");
        sb.AppendLine("                if (stateHandle.IsAllocated) stateHandle.Free();");
        sb.AppendLine("                throw;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            return await tcs.Task;");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void EmitLegacyCreateAsyncCall(
        StringBuilder sb, ViewBridgeInfo info, AsyncViewPattern pattern, string indent)
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
                nativeArgs.Add($"{param.Name} ? 1 : 0");
            }
            else
            {
                nativeArgs.Add(param.Name);
            }
        }
        nativeArgs.Add("(IntPtr)readyPtr");
        nativeArgs.Add("(IntPtr)errorPtr");
        if (pattern.HasResultCallback)
        {
            nativeArgs.Add("(IntPtr)resultPtr");
        }
        nativeArgs.Add("GCHandle.ToIntPtr(stateHandle)");

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
/// Configuration for a known async View bridge pattern.
/// When ConstructionChain is null, legacy hard-coded emission is used (BlinkIDUX).
/// When ConstructionChain is non-null, data-driven emission iterates the chain steps.
/// </summary>
public record AsyncViewPattern(
    string ViewName,
    string SessionClassName,
    string[] ExtraSwiftImports,
    AsyncSessionField[] SessionFields,
    AsyncFlatParam[] FlattenedParams,
    bool HasResultCallback,
    List<AsyncConstructionStep>? ConstructionChain = null);

/// <summary>
/// A single step in an async construction chain.
/// Each step represents creating one intermediate object that the View depends on.
/// </summary>
public record AsyncConstructionStep(
    string VariableName,
    string SwiftTypeName,
    bool IsAsync,
    bool Throws,
    List<ConstructionArg> Args,
    string? FactoryMethod = null);

/// <summary>
/// An argument to a construction step.
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
public record AsyncSessionField(string Name, string SwiftType);

/// <summary>
/// A flattened parameter for the async Create function.
/// </summary>
public record AsyncFlatParam(
    string Name,
    AsyncFlatParamKind Kind,
    string SwiftAbiType,
    string CSharpPInvokeType,
    string? SwiftConversion,
    string? CSharpConversion);

/// <summary>
/// Kind of flattened async parameter.
/// </summary>
public enum AsyncFlatParamKind
{
    String,
    Bool,
    Primitive,
}
