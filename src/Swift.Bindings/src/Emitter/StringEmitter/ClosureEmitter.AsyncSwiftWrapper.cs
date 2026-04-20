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

            // Sendable shim for the (contextPtr, startFunc) pair that C# passes in
            // place of an async closure. UnsafeMutableRawPointer is non-Sendable in
            // Swift 6, so we ferry the pair across Task {} via this @unchecked
            // Sendable struct. Safe because: (a) the context pointer is owned by
            // C# for the lifetime of the outer call, (b) the start function is a
            // stable @_cdecl symbol with no mutable captured state.
            private struct _SBW_AsyncClosureHandoff: @unchecked Sendable {
                let contextPtr: UnsafeMutableRawPointer
                let startFunc: @convention(c) (UnsafeMutableRawPointer,
                                               UnsafeMutableRawPointer,
                                               UnsafeMutableRawPointer,
                                               UnsafeMutableRawPointer) -> Void
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
        return $"let {handoffVar} = _SBW_AsyncClosureHandoff(contextPtr: {paramName}ContextPtr, startFunc: {paramName}StartFunc)";
    }

    /// <summary>
    /// Swift block that constructs the <c>adapted_&lt;name&gt;</c> closure inside
    /// <c>Task { }</c>, before the <c>try await &lt;target&gt;</c> line. The
    /// adapter routes Swift's <c>() async throws -&gt; T</c> into the C# start
    /// thunk via <c>withCheckedThrowingContinuation</c>.
    /// </summary>
    /// <param name="indent">Whitespace prefix applied to every emitted line.</param>
    public static string BuildAsyncClosureAdapter(
        string paramName,
        string moduleName,
        string swiftReturnType,
        string indent)
    {
        var sanitizedModule = SanitizeModuleName(moduleName);
        var hash = EmitterUtility.DeterministicHash8(swiftReturnType);
        var boxClassName = GetAsyncClosureBoxClassName(sanitizedModule, hash);
        var symbolRoot = GetAsyncClosureSymbolRoot(sanitizedModule, hash);
        var handoffVar = GetAsyncClosureHandoffVarName(paramName);
        var adaptedVar = GetAdaptedClosureVarName(paramName);

        return $$"""
            {{indent}}// Adapter closure for async-throwing closure parameter '{{paramName}}'.
            {{indent}}// Bridges Swift's `() async throws -> {{swiftReturnType}}` back into the C#
            {{indent}}// start thunk via a CheckedContinuation owned by the per-T box class.
            {{indent}}let {{adaptedVar}}: @Sendable () async throws -> {{swiftReturnType}} = {
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
            {{indent}}        {{handoffVar}}.startFunc({{handoffVar}}.contextPtr, boxPtr, successFP, errorFP)
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
