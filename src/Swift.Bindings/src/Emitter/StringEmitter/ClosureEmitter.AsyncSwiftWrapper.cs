// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Swift-side emission for async-closure bridge helpers (Session A baseline).
///
/// Session A scope: <c>@escaping () async throws -&gt; T</c> closure parameters
/// on async-throwing outer methods where T is a BitwiseCopyable primitive.
/// Emits three tiers:
///   (1) per Swift module — SwiftBindingsBridgeError + Sendable handoff shim,
///   (2) per (module, T) — continuation box class + success/error @_cdecl callbacks,
///   (3) per closure site — handoff init outside Task {} and an adapter closure
///       inside it that routes the Swift side back into the C# start thunk.
/// Dedup lives on <see cref="ModuleEmissionContext"/> (per-module).
/// </summary>
public static partial class ClosureEmitter
{
    /// <summary>
    /// Emits <c>SwiftBindingsBridgeError</c> and the <c>_SBW_AsyncClosureHandoff</c>
    /// Sendable shim once per Swift module. No-op on subsequent calls.
    /// </summary>
    public static bool EmitAsyncClosureBridgePreambleIfNeeded(SwiftWriter swiftWriter, ModuleEmissionContext ctx)
    {
        if (ctx.AsyncClosureBridgeErrorEmitted)
            return false;

        swiftWriter.WriteLines("""
            // Error type bridged back into Swift when a C# async closure throws.
            // `LocalizedError` makes `error.localizedDescription` surface the C#
            // exception message cleanly at Swift call sites.
            public struct SwiftBindingsBridgeError: LocalizedError, CustomStringConvertible {
                public let description: String
                public var errorDescription: String? { description }
                public init(_ description: String) { self.description = description }
            }

            // Sendable shim for the (contextPtr, startFuncPtr) pair that C# passes in
            // place of an async closure. UnsafeMutableRawPointer is non-Sendable in
            // Swift 6, so we ferry the pair across Task {} via this @unchecked
            // Sendable struct. Safe because: (a) the context pointer is owned by
            // C# for the lifetime of the outer call, (b) the start function is a
            // stable @_cdecl symbol with no mutable captured state.
            // startFuncPtr is opaque — each adapter site unsafeBitCasts it to its
            // own per-arity @convention(c) signature.
            private struct _SBW_AsyncClosureHandoff: @unchecked Sendable {
                let contextPtr: UnsafeMutableRawPointer
                let startFuncPtr: UnsafeMutableRawPointer
            }

            """);

        ctx.AsyncClosureBridgeErrorEmitted = true;
        return true;
    }

    /// <summary>
    /// Emits the continuation box class and success/error <c>@_cdecl</c> resume
    /// callbacks for a given (module, T) pair. Deduped via
    /// <see cref="ModuleEmissionContext.TryAddAsyncClosureSwiftWrapperKey"/>.
    /// </summary>
    public static bool EmitAsyncClosureBoxIfNeeded(
        SwiftWriter swiftWriter,
        string moduleName,
        string swiftReturnType,
        ModuleEmissionContext ctx)
    {
        var sanitizedModule = SanitizeModuleName(moduleName);
        var hash = EmitterUtility.DeterministicHash8(swiftReturnType);
        var dedupKey = $"{sanitizedModule}|{hash}";
        if (!ctx.TryAddAsyncClosureSwiftWrapperKey(dedupKey))
            return false;

        var boxClassName = GetAsyncClosureBoxClassName(sanitizedModule, hash);
        var symbolRoot = GetAsyncClosureSymbolRoot(sanitizedModule, hash);

        swiftWriter.WriteLines($$"""
            // Continuation box retained across the C# start-thunk call. The C#
            // helper resumes exactly once via the paired success/error symbols.
            private final class {{boxClassName}} {
                let cont: CheckedContinuation<{{swiftReturnType}}, Error>
                init(_ cont: CheckedContinuation<{{swiftReturnType}}, Error>) { self.cont = cont }
            }

            @_cdecl("{{symbolRoot}}_success")
            internal func {{symbolRoot}}_success(
                _ boxPtr: UnsafeMutableRawPointer,
                _ resultPtr: UnsafeMutableRawPointer
            ) {
                let box = Unmanaged<{{boxClassName}}>.fromOpaque(boxPtr).takeRetainedValue()
                let value = resultPtr.load(as: {{swiftReturnType}}.self)
                box.cont.resume(returning: value)
            }

            @_cdecl("{{symbolRoot}}_error")
            internal func {{symbolRoot}}_error(
                _ boxPtr: UnsafeMutableRawPointer,
                _ msgPtr: UnsafePointer<CChar>
            ) {
                let box = Unmanaged<{{boxClassName}}>.fromOpaque(boxPtr).takeRetainedValue()
                box.cont.resume(throwing: SwiftBindingsBridgeError(String(cString: msgPtr)))
            }

            """);

        return true;
    }

    /// <summary>
    /// Swift line that materialises the <c>_SBW_AsyncClosureHandoff</c> struct
    /// for a single closure parameter. Emit BEFORE <c>Task { }</c> — the task
    /// closure must capture a Sendable value; capturing the raw
    /// UnsafeMutableRawPointer directly would be a Swift 6 concurrency error.
    /// </summary>
    public static string BuildAsyncClosureHandoffInit(string paramName)
    {
        var handoffVar = GetAsyncClosureHandoffVarName(paramName);
        // The typed @convention(c) startFunc arrives with a per-arity signature. Erase
        // it to a raw pointer here so the Sendable shim has a single shape across all
        // call sites; each adapter casts back to its own signature.
        return $"let {handoffVar} = _SBW_AsyncClosureHandoff(contextPtr: {paramName}ContextPtr, startFuncPtr: unsafeBitCast({paramName}StartFunc, to: UnsafeMutableRawPointer.self))";
    }

    /// <summary>
    /// Info for a single async-throwing closure arg used when building the Swift
    /// adapter body. Populated from <see cref="ClosureHandler.GetAsyncThrowingArgCategory"/>.
    /// <list type="bullet">
    /// <item><c>ParamName</c>: Swift-side parameter name (<c>a0</c>, <c>a1</c>, …) bound inside the adapter.</item>
    /// <item><c>SwiftSignatureType</c>: type as rendered in the adapter's Swift signature (<c>String</c>, <c>MyClass</c>, <c>Int32</c>).</item>
    /// <item><c>AbiType</c>: type in the <c>@convention(c)</c> startFunc signature (<c>UnsafeMutableRawPointer</c> for reference/string, else primitive).</item>
    /// <item><c>Category</c>: drives the marshalling expression inside <c>withCheckedThrowingContinuation</c>.</item>
    /// </list>
    /// </summary>
    public readonly struct AsyncClosureArgInfo
    {
        public string ParamName { get; }
        public string SwiftSignatureType { get; }
        public string AbiType { get; }
        public ClosureHandler.AsyncThrowingArgCategory Category { get; }

        public AsyncClosureArgInfo(
            string paramName,
            string swiftSignatureType,
            string abiType,
            ClosureHandler.AsyncThrowingArgCategory category)
        {
            ParamName = paramName;
            SwiftSignatureType = swiftSignatureType;
            AbiType = abiType;
            Category = category;
        }
    }

    /// <summary>
    /// Swift block that constructs the <c>adapted_&lt;name&gt;</c> closure inside
    /// <c>Task { }</c>, before the <c>try await &lt;target&gt;</c> line. The
    /// adapter routes Swift's <c>(A0, A1, …) async throws -&gt; T</c> into the C#
    /// start thunk via <c>withCheckedThrowingContinuation</c>. Args are marshalled
    /// synchronously inside the continuation body (primitive: pass-through, String:
    /// nested <c>withUnsafePointer</c>, class: <c>Unmanaged.passUnretained.toOpaque</c>).
    /// </summary>
    /// <param name="indent">Whitespace prefix applied to every emitted line.</param>
    public static string BuildAsyncClosureAdapter(
        string paramName,
        string moduleName,
        string swiftReturnType,
        IReadOnlyList<AsyncClosureArgInfo> args,
        string indent)
    {
        var sanitizedModule = SanitizeModuleName(moduleName);
        var hash = EmitterUtility.DeterministicHash8(swiftReturnType);
        var boxClassName = GetAsyncClosureBoxClassName(sanitizedModule, hash);
        var symbolRoot = GetAsyncClosureSymbolRoot(sanitizedModule, hash);
        var handoffVar = GetAsyncClosureHandoffVarName(paramName);
        var adaptedVar = GetAdaptedClosureVarName(paramName);

        // Per-arity @Sendable closure signature. Zero args -> `()`, otherwise `(A0, A1, …)`.
        var closureParamList = args.Count == 0
            ? "()"
            : "(" + string.Join(", ", args.Select(a => a.SwiftSignatureType)) + ")";
        var closureParamBindings = args.Count == 0
            ? ""
            : "(" + string.Join(", ", args.Select(a => a.ParamName)) + ") in ";

        // Per-arity @convention(c) startFunc ABI type. Args appear BETWEEN (ctx, box)
        // and (successFP, errorFP) to match the C# Start thunk layout.
        var startAbiParams = "UnsafeMutableRawPointer, UnsafeMutableRawPointer";
        foreach (var a in args)
            startAbiParams += ", " + a.AbiType;
        startAbiParams += ", UnsafeMutableRawPointer, UnsafeMutableRawPointer";

        // Each arg's call-site expression and the set of `withUnsafePointer` nests we
        // need to wrap the final startFunc call in (only Strings need one).
        var argCallExprs = new List<string>();
        var stringNests = new List<AsyncClosureArgInfo>();
        foreach (var a in args)
        {
            switch (a.Category)
            {
                case ClosureHandler.AsyncThrowingArgCategory.Primitive:
                    argCallExprs.Add(a.ParamName);
                    break;
                case ClosureHandler.AsyncThrowingArgCategory.SwiftString:
                    stringNests.Add(a);
                    argCallExprs.Add($"UnsafeMutableRawPointer(mutating: {a.ParamName}Ptr)");
                    break;
                case ClosureHandler.AsyncThrowingArgCategory.SwiftClass:
                    argCallExprs.Add($"Unmanaged.passUnretained({a.ParamName}).toOpaque()");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"AsyncClosureArgInfo '{a.ParamName}' has unsupported category {a.Category}");
            }
        }

        var callArgList = string.Join(", ", argCallExprs);
        var innerIndent = indent + "        ";

        // Emit the innermost call: typedStart(ctx, box, a0_raw, a1_raw, …, successFP, errorFP)
        string callLine;
        if (args.Count == 0)
        {
            callLine = $"{innerIndent}typedStart({handoffVar}.contextPtr, boxPtr, successFP, errorFP)";
        }
        else
        {
            callLine = $"{innerIndent}typedStart({handoffVar}.contextPtr, boxPtr, {callArgList}, successFP, errorFP)";
        }

        // Nest `withUnsafePointer(to: aN) { aNPtr in … }` blocks for each String arg.
        // Outermost first, innermost deepest. The deepest line is the typedStart(…) call.
        string innerBlock = callLine;
        if (stringNests.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < stringNests.Count; i++)
            {
                var openIndent = innerIndent + new string(' ', 4 * i);
                sb.AppendLine($"{openIndent}withUnsafePointer(to: {stringNests[i].ParamName}) {{ ({stringNests[i].ParamName}Ptr: UnsafePointer<{stringNests[i].SwiftSignatureType}>) in");
            }
            var deepestIndent = innerIndent + new string(' ', 4 * stringNests.Count);
            sb.AppendLine(args.Count == 0
                ? $"{deepestIndent}typedStart({handoffVar}.contextPtr, boxPtr, successFP, errorFP)"
                : $"{deepestIndent}typedStart({handoffVar}.contextPtr, boxPtr, {callArgList}, successFP, errorFP)");
            for (int i = stringNests.Count - 1; i >= 0; i--)
            {
                var closeIndent = innerIndent + new string(' ', 4 * i);
                sb.AppendLine($"{closeIndent}}}");
            }
            innerBlock = sb.ToString().TrimEnd('\r', '\n');
        }

        return $$"""
            {{indent}}// Adapter closure for async-throwing closure parameter '{{paramName}}'.
            {{indent}}// Bridges Swift's `{{closureParamList}} async throws -> {{swiftReturnType}}` back into
            {{indent}}// the C# start thunk via a CheckedContinuation owned by the per-T box class.
            {{indent}}let {{adaptedVar}}: @Sendable {{closureParamList}} async throws -> {{swiftReturnType}} = { {{closureParamBindings}}
            {{indent}}    return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<{{swiftReturnType}}, Error>) in
            {{indent}}        let box = {{boxClassName}}(cont)
            {{indent}}        let boxPtr = Unmanaged.passRetained(box).toOpaque()
            {{indent}}        let successFP = unsafeBitCast(
            {{indent}}            {{symbolRoot}}_success as
            {{indent}}                @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void,
            {{indent}}            to: UnsafeMutableRawPointer.self)
            {{indent}}        let errorFP = unsafeBitCast(
            {{indent}}            {{symbolRoot}}_error as
            {{indent}}                @convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void,
            {{indent}}            to: UnsafeMutableRawPointer.self)
            {{indent}}        let typedStart = unsafeBitCast({{handoffVar}}.startFuncPtr,
            {{indent}}            to: (@convention(c) ({{startAbiParams}}) -> Void).self)
            {{innerBlock}}
            {{indent}}    }
            {{indent}}}
            """;
    }

    /// <summary>
    /// Variable name for the adapted Swift closure that substitutes for the
    /// original async-throwing closure arg in the target method call.
    /// </summary>
    public static string GetAdaptedClosureVarName(string paramName) => $"_SBWAdapted_{paramName}";

    private static string GetAsyncClosureHandoffVarName(string paramName) => $"_SBWHandoff_{paramName}";

    private static string GetAsyncClosureBoxClassName(string sanitizedModule, string hash)
        => $"_SBW_{sanitizedModule}_AsyncBox_{hash}";

    private static string GetAsyncClosureSymbolRoot(string sanitizedModule, string hash)
        => $"_SBW_{sanitizedModule}_asyncBox_{hash}";

    private static string SanitizeModuleName(string moduleName)
        => moduleName.Replace('.', '_').Replace('-', '_').Replace(' ', '_');
}
