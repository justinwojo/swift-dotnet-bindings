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

    #region Async Swift Generation

    internal static void EmitAsyncSwiftBridge(
        StringBuilder sb, string moduleName, ViewBridgeInfo info, AsyncViewPattern pattern)
    {
        var prefix = $"SBW_{moduleName}_{info.ViewName}";
        var sessionClass = $"{prefix}_Session";
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

        // Session class
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
        sb.AppendLine($"        let session = Unmanaged<{sessionClass}>.fromOpaque(handle).takeUnretainedValue()");
        if (pattern.HasResultCallback)
        {
            sb.AppendLine($"        session.cancelResultMonitor()");
        }
        sb.AppendLine($"        Unmanaged<{sessionClass}>.fromOpaque(handle).release()");
        sb.AppendLine("    }");
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

        // CreateAsync factory
        EmitCreateAsyncFactory(sb, info, pattern);

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

    private static void EmitCreateAsyncFactory(
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
            EmitCreateAsyncCall(sb, info, pattern, "                        ");
            sb.AppendLine("                    }");
        }
        else
        {
            EmitCreateAsyncCall(sb, info, pattern, "                    ");
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

    private static void EmitCreateAsyncCall(
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
/// </summary>
public record AsyncViewPattern(
    string ViewName,
    string SessionClassName,
    string[] ExtraSwiftImports,
    AsyncSessionField[] SessionFields,
    AsyncFlatParam[] FlattenedParams,
    bool HasResultCallback);

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
