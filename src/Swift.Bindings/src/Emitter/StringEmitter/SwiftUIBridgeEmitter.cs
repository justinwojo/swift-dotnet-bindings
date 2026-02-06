// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Emits SwiftUI bridge files (Swift wrappers + C# interop) for detected View types.
/// </summary>
public static partial class SwiftUIBridgeEmitter
{
    /// <summary>
    /// Emits bridge files for collected SwiftUI View types.
    /// Generates {namespace}.SwiftUIBridge.swift and {namespace}.SwiftUIBridge.cs
    /// in the specified output directory.
    /// </summary>
    public static void EmitBridgeFiles(
        string outputDirectory,
        string @namespace,
        string moduleName,
        IReadOnlyList<TypeDecl> collectedViews,
        ILogger logger,
        ITypeDatabase? typeDatabase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(collectedViews);
        ArgumentNullException.ThrowIfNull(logger);

        if (collectedViews.Count == 0)
            return;

        var context = new BridgeContext(typeDatabase);
        var viewInfos = collectedViews.Select(v => AnalyzeView(v, moduleName)).ToList();

        // Determine which views can be functionally bridged
        var bridgeResults = new List<(ViewBridgeInfo Info, List<BridgeParameter>? Params, AsyncViewPattern? AsyncPattern, bool IsFunctional)>();
        foreach (var info in viewInfos)
        {
            List<BridgeParameter>? bridgeParams = null;
            AsyncViewPattern? asyncPattern = null;
            bool isFunctional = false;

            if (info.Classification == ViewInitClassification.AsyncDependency)
            {
                asyncPattern = GetAsyncPattern(info.ViewName, info.ModuleName);
                isFunctional = asyncPattern != null;
            }
            else if (info.Classification == ViewInitClassification.Simple && info.Constructors.Count > 0)
            {
                bridgeParams = AnalyzeInitParameters(info.Constructors[0], context);
                isFunctional = bridgeParams != null;
            }
            else if (info.Classification == ViewInitClassification.Simple && info.Constructors.Count == 0)
            {
                bridgeParams = new List<BridgeParameter>();
                isFunctional = true;
            }

            bridgeResults.Add((info, bridgeParams, asyncPattern, isFunctional));
        }

        // Record bridged views in report
        foreach (var (info, _, _, isFunctional) in bridgeResults)
        {
            var status = isFunctional ? "Generated" : "TemplatePending";
            ReportCollector.RecordBridgedView(
                info.ViewName, moduleName, info.Classification.ToString(), status);
        }

        var swiftContent = GenerateSwiftBridge(moduleName, bridgeResults);
        var csContent = GenerateCSharpBridge(@namespace, moduleName, bridgeResults);

        var swiftPath = Path.Combine(outputDirectory, $"{@namespace}.SwiftUIBridge.swift");
        File.WriteAllText(swiftPath, swiftContent);

        var csPath = Path.Combine(outputDirectory, $"{@namespace}.SwiftUIBridge.cs");
        File.WriteAllText(csPath, csContent);

        var functionalCount = bridgeResults.Count(r => r.IsFunctional);
        var templateCount = bridgeResults.Count - functionalCount;
        logger.LogInformation(
            "SwiftUI bridge files written: {Functional} functional, {Template} templates",
            functionalCount, templateCount);
    }

    /// <summary>
    /// Analyzes a View type and classifies its init for bridge generation.
    /// </summary>
    public static ViewBridgeInfo AnalyzeView(TypeDecl viewType, string moduleName)
    {
        var constructors = viewType.Methods.Where(m => m.IsConstructor).ToList();

        if (viewType.IsGeneric)
        {
            return new ViewBridgeInfo(viewType.Name, moduleName, ViewInitClassification.Unsupported,
                "Generic type parameter", constructors);
        }

        // Check for known async dependency patterns (v1: explicit matching only).
        // Must be checked before Simple classification since async views have a
        // different code generation path regardless of their constructor shape.
        if (KnownAsyncPatterns.ContainsKey($"{moduleName}.{viewType.Name}"))
        {
            return new ViewBridgeInfo(viewType.Name, moduleName, ViewInitClassification.AsyncDependency,
                null, constructors);
        }

        if (constructors.Count == 0)
        {
            return new ViewBridgeInfo(viewType.Name, moduleName, ViewInitClassification.Simple,
                null, constructors);
        }

        // Check each constructor's parameters
        foreach (var ctor in constructors)
        {
            // CSSignature[0] is the return type, skip it
            for (int i = 1; i < ctor.CSSignature.Count; i++)
            {
                var param = ctor.CSSignature[i];

                if (param.IsGeneric)
                {
                    return new ViewBridgeInfo(viewType.Name, moduleName, ViewInitClassification.Unsupported,
                        "Generic parameter in init", constructors);
                }

                if (param.SwiftTypeSpec is ProtocolListTypeSpec)
                {
                    return new ViewBridgeInfo(viewType.Name, moduleName, ViewInitClassification.Unsupported,
                        "Existential parameter in init", constructors);
                }
            }
        }

        return new ViewBridgeInfo(viewType.Name, moduleName, ViewInitClassification.Simple,
            null, constructors);
    }

    #region Swift Generation

    private static string GenerateSwiftBridge(
        string moduleName,
        List<(ViewBridgeInfo Info, List<BridgeParameter>? Params, AsyncViewPattern? AsyncPattern, bool IsFunctional)> bridgeResults)
    {
        var sb = new StringBuilder();
        bool hasFunctionalBridge = bridgeResults.Any(r => r.IsFunctional);

        if (hasFunctionalBridge)
        {
            sb.AppendLine("// Auto-generated by SwiftBindings — SwiftUI Bridge");
            sb.AppendLine("import UIKit");
            sb.AppendLine("import SwiftUI");
            sb.AppendLine($"import {moduleName}");

            // Extra imports from async patterns
            var extraImports = bridgeResults
                .Where(r => r.AsyncPattern?.ExtraSwiftImports != null)
                .SelectMany(r => r.AsyncPattern!.ExtraSwiftImports)
                .Distinct()
                .Where(m => m != moduleName);
            foreach (var import in extraImports)
            {
                sb.AppendLine($"import {import}");
            }

            sb.AppendLine();

            // Shared helpers
            sb.AppendLine("// --- Shared helpers ---");
            sb.AppendLine();
            sb.AppendLine("@discardableResult");
            sb.AppendLine("func SBW_onMainThread<T>(_ block: () -> T) -> T {");
            sb.AppendLine("    if Thread.isMainThread { return block() }");
            sb.AppendLine("    return DispatchQueue.main.sync { block() }");
            sb.AppendLine("}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("// Auto-generated by SwiftBindings — SwiftUI Bridge Templates");
            sb.AppendLine("// These templates require manual completion before use.");
            sb.AppendLine("import UIKit");
            sb.AppendLine("import SwiftUI");
            sb.AppendLine($"import {moduleName}");
            sb.AppendLine();
        }

        foreach (var (info, bridgeParams, asyncPattern, isFunctional) in bridgeResults)
        {
            if (isFunctional && asyncPattern != null)
            {
                EmitAsyncSwiftBridge(sb, moduleName, info, asyncPattern);
            }
            else if (isFunctional)
            {
                EmitFunctionalSwiftBridge(sb, moduleName, info, bridgeParams!);
            }
            else
            {
                EmitSwiftTemplate(sb, moduleName, info);
            }
        }

        return sb.ToString();
    }

    private static void EmitFunctionalSwiftBridge(
        StringBuilder sb, string moduleName, ViewBridgeInfo info, List<BridgeParameter> bridgeParams)
    {
        var prefix = $"SBW_{moduleName}_{info.ViewName}";
        var sessionClass = $"{prefix}_Session";
        var handlesVar = $"{prefix}_liveHandles";

        sb.AppendLine($"// --- {info.ViewName} ---");
        sb.AppendLine();

        // Session class
        sb.AppendLine($"final class {sessionClass} {{");
        sb.AppendLine($"    let hostingController: UIHostingController<{info.ViewName}>");

        // Store callback fields
        foreach (var param in bridgeParams)
        {
            if (param.Kind == BridgeParameterKind.VoidClosure)
            {
                sb.AppendLine($"    let {param.Name}Callback: ({param.SwiftAbiType.TrimEnd('?')})?");
                sb.AppendLine($"    let {param.Name}UserData: UnsafeMutableRawPointer?");
            }
        }

        sb.AppendLine();

        // Init
        sb.Append($"    init(");
        var initParams = new List<string>();
        foreach (var param in bridgeParams)
        {
            if (param.Kind == BridgeParameterKind.VoidClosure)
            {
                initParams.Add($"{param.Name}Callback: {param.SwiftAbiType}");
                initParams.Add($"{param.Name}UserData: UnsafeMutableRawPointer?");
            }
            else if (param.Kind == BridgeParameterKind.String)
            {
                initParams.Add($"{param.Name}Ptr: UnsafePointer<UInt8>?");
                initParams.Add($"{param.Name}Len: Int");
            }
            else if (param.Kind == BridgeParameterKind.OptionalWrapped)
            {
                initParams.Add($"{param.Name}HasValue: Int32");
                initParams.Add($"{param.Name}Value: {param.InnerParameter!.SwiftAbiType}");
            }
            else
            {
                initParams.Add($"{param.Name}: {param.SwiftAbiType}");
            }
        }
        sb.Append(string.Join(",\n         ", initParams));
        sb.AppendLine(") {");

        // Store callbacks
        foreach (var param in bridgeParams)
        {
            if (param.Kind == BridgeParameterKind.VoidClosure)
            {
                sb.AppendLine($"        self.{param.Name}Callback = {param.Name}Callback");
                sb.AppendLine($"        self.{param.Name}UserData = {param.Name}UserData");
            }
        }

        // Capture locals for closure safety
        foreach (var param in bridgeParams)
        {
            if (param.Kind == BridgeParameterKind.VoidClosure)
            {
                sb.AppendLine($"        let cb_{param.Name} = {param.Name}Callback; let ud_{param.Name} = {param.Name}UserData");
            }
        }

        // Build view init call
        var viewInitArgs = new List<string>();
        foreach (var param in bridgeParams)
        {
            if (param.Kind == BridgeParameterKind.VoidClosure)
            {
                viewInitArgs.Add($"{param.Name}: {{\n            DispatchQueue.main.async {{ cb_{param.Name}?(ud_{param.Name}) }}\n        }}");
            }
            else if (param.Kind == BridgeParameterKind.String)
            {
                sb.AppendLine($"        let {param.Name}String: String");
                sb.AppendLine($"        if let ptr = {param.Name}Ptr, {param.Name}Len > 0 {{");
                sb.AppendLine($"            {param.Name}String = String(bytes: UnsafeBufferPointer(start: ptr, count: {param.Name}Len), encoding: .utf8) ?? \"\"");
                sb.AppendLine($"        }} else {{");
                sb.AppendLine($"            {param.Name}String = \"\"");
                sb.AppendLine($"        }}");
                viewInitArgs.Add($"{param.Name}: {param.Name}String");
            }
            else if (param.Kind == BridgeParameterKind.BoundEnum)
            {
                // Construct enum from rawValue
                viewInitArgs.Add($"{param.Name}: {param.BridgeTypeName}(rawValue: {param.Name})!");
            }
            else if (param.Kind == BridgeParameterKind.OptionalWrapped)
            {
                var inner = param.InnerParameter!;
                string valueExpr;
                if (inner.Kind == BridgeParameterKind.BoundEnum)
                    valueExpr = $"{inner.BridgeTypeName}(rawValue: {param.Name}Value)!";
                else if (inner.SwiftConversion != null)
                    valueExpr = $"{param.Name}Value {inner.SwiftConversion}";
                else
                    valueExpr = $"{param.Name}Value";
                viewInitArgs.Add($"{param.Name}: {param.Name}HasValue != 0 ? {valueExpr} : nil");
            }
            else if (param.Kind == BridgeParameterKind.Primitive && param.SwiftConversion != null)
            {
                // Bool: Int32 != 0
                viewInitArgs.Add($"{param.Name}: {param.Name} {param.SwiftConversion}");
            }
            else
            {
                viewInitArgs.Add($"{param.Name}: {param.Name}");
            }
        }

        if (viewInitArgs.Count == 0)
        {
            sb.AppendLine($"        let view = {info.ViewName}()");
        }
        else
        {
            sb.AppendLine($"        let view = {info.ViewName}({string.Join(", ", viewInitArgs)})");
        }
        sb.AppendLine($"        self.hostingController = UIHostingController(rootView: view)");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Handle tracking
        sb.AppendLine($"var {handlesVar} = Set<UnsafeMutableRawPointer>()");
        sb.AppendLine();

        // Create function
        sb.AppendLine($"@_cdecl(\"{prefix}_Create\")");
        sb.Append($"public func {prefix}_Create(");
        var createParams = new List<string>();
        foreach (var param in bridgeParams)
        {
            if (param.Kind == BridgeParameterKind.VoidClosure)
            {
                createParams.Add($"_ {param.Name}Callback: {param.SwiftAbiType}");
                createParams.Add($"_ {param.Name}UserData: UnsafeMutableRawPointer?");
            }
            else if (param.Kind == BridgeParameterKind.String)
            {
                createParams.Add($"_ {param.Name}Ptr: UnsafePointer<UInt8>?");
                createParams.Add($"_ {param.Name}Len: Int");
            }
            else if (param.Kind == BridgeParameterKind.OptionalWrapped)
            {
                createParams.Add($"_ {param.Name}HasValue: Int32");
                createParams.Add($"_ {param.Name}Value: {param.InnerParameter!.SwiftAbiType}");
            }
            else
            {
                createParams.Add($"_ {param.Name}: {param.SwiftAbiType}");
            }
        }
        sb.AppendLine(string.Join(",\n    ", createParams));
        sb.AppendLine(") -> UnsafeMutableRawPointer? {");
        sb.AppendLine("    return SBW_onMainThread {");

        // Build session init call
        var sessionArgs = new List<string>();
        foreach (var param in bridgeParams)
        {
            if (param.Kind == BridgeParameterKind.VoidClosure)
            {
                sessionArgs.Add($"{param.Name}Callback: {param.Name}Callback");
                sessionArgs.Add($"{param.Name}UserData: {param.Name}UserData");
            }
            else if (param.Kind == BridgeParameterKind.String)
            {
                sessionArgs.Add($"{param.Name}Ptr: {param.Name}Ptr");
                sessionArgs.Add($"{param.Name}Len: {param.Name}Len");
            }
            else if (param.Kind == BridgeParameterKind.OptionalWrapped)
            {
                sessionArgs.Add($"{param.Name}HasValue: {param.Name}HasValue");
                sessionArgs.Add($"{param.Name}Value: {param.Name}Value");
            }
            else
            {
                sessionArgs.Add($"{param.Name}: {param.Name}");
            }
        }

        sb.AppendLine($"        let session = {sessionClass}(");
        sb.AppendLine($"            {string.Join(",\n            ", sessionArgs)})");
        sb.AppendLine($"        let handle = Unmanaged.passRetained(session).toOpaque()");
        sb.AppendLine($"        {handlesVar}.insert(handle)");
        sb.AppendLine($"        return handle");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

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
        sb.AppendLine($"        Unmanaged<{sessionClass}>.fromOpaque(handle).release()");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitSwiftTemplate(StringBuilder sb, string moduleName, ViewBridgeInfo info)
    {
        sb.AppendLine($"// ========================================");
        sb.AppendLine($"// BRIDGE TEMPLATE: {info.ViewName}");
        sb.AppendLine($"// ========================================");

        var initDesc = GetInitDescription(info);
        sb.AppendLine($"// Init: {initDesc}");
        sb.AppendLine($"// Classification: {info.Classification}");
        if (info.UnsupportedReason != null)
            sb.AppendLine($"// Unsupported reason: {info.UnsupportedReason}");
        sb.AppendLine($"// Status: Template — complete the TODO sections");
        sb.AppendLine($"//");
        sb.AppendLine($"// @_cdecl(\"SBW_{moduleName}_{info.ViewName}_Create\")");
        sb.AppendLine($"// @_cdecl(\"SBW_{moduleName}_{info.ViewName}_GetViewController\")");
        sb.AppendLine($"// @_cdecl(\"SBW_{moduleName}_{info.ViewName}_Free\")");
        sb.AppendLine($"//");
        sb.AppendLine($"// See: src/docs/swiftui-bridge-design.md");
        sb.AppendLine();
    }

    #endregion

    #region C# Generation

    private static string GenerateCSharpBridge(
        string @namespace,
        string moduleName,
        List<(ViewBridgeInfo Info, List<BridgeParameter>? Params, AsyncViewPattern? AsyncPattern, bool IsFunctional)> bridgeResults)
    {
        var sb = new StringBuilder();
        bool hasFunctionalBridge = bridgeResults.Any(r => r.IsFunctional);

        if (hasFunctionalBridge)
        {
            sb.AppendLine("// Auto-generated by SwiftBindings — SwiftUI Bridge");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine("using System.Runtime.InteropServices;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine();
            sb.AppendLine($"namespace {@namespace}");
            sb.AppendLine("{");

            foreach (var (info, bridgeParams, asyncPattern, isFunctional) in bridgeResults)
            {
                if (isFunctional && asyncPattern != null)
                {
                    EmitAsyncCSharpBridge(sb, moduleName, info, asyncPattern);
                }
                else if (isFunctional)
                {
                    EmitFunctionalCSharpBridge(sb, moduleName, info, bridgeParams!);
                }
                else
                {
                    EmitCSharpTemplate(sb, info);
                }
            }

            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine("// Auto-generated by SwiftBindings — SwiftUI Bridge Templates");
            sb.AppendLine("// These templates require manual completion before use.");
            sb.AppendLine("//");
            sb.AppendLine("// Views detected:");
            foreach (var (info, _, _, _) in bridgeResults)
            {
                var paramSummary = GetInitParamSummary(info);
                sb.AppendLine($"//   - {info.ViewName} ({info.Classification}: {paramSummary})");
            }
            sb.AppendLine("//");
            sb.AppendLine("// See: src/docs/swiftui-bridge-design.md");
        }

        sb.AppendLine();

        return sb.ToString();
    }

    private static void EmitFunctionalCSharpBridge(
        StringBuilder sb, string moduleName, ViewBridgeInfo info, List<BridgeParameter> bridgeParams)
    {
        var prefix = $"SBW_{moduleName}_{info.ViewName}";
        var bridgeLib = $"{moduleName}Bridge";
        var hasClosures = bridgeParams.Any(p => p.Kind == BridgeParameterKind.VoidClosure);
        var hasStrings = bridgeParams.Any(p => p.Kind == BridgeParameterKind.String);
        var needsUnsafe = hasClosures || hasStrings;

        // NativeMethods class
        sb.AppendLine($"    internal static class {info.ViewName}BridgeNativeMethods");
        sb.AppendLine("    {");
        sb.AppendLine($"        private const string BridgeLib = \"{bridgeLib}\";");
        sb.AppendLine();

        // Create P/Invoke
        sb.AppendLine($"        [DllImport(BridgeLib, EntryPoint = \"{prefix}_Create\")]");
        sb.AppendLine($"        [UnmanagedCallConv(CallConvs = new[] {{ typeof(CallConvCdecl) }})]");

        var createPInvokeParams = new List<string>();
        foreach (var param in bridgeParams)
        {
            if (param.Kind == BridgeParameterKind.VoidClosure)
            {
                createPInvokeParams.Add($"IntPtr {param.Name}Callback");
                createPInvokeParams.Add($"IntPtr {param.Name}UserData");
            }
            else if (param.Kind == BridgeParameterKind.String)
            {
                createPInvokeParams.Add($"IntPtr {param.Name}Ptr");
                createPInvokeParams.Add($"nint {param.Name}Len");
            }
            else if (param.Kind == BridgeParameterKind.OptionalWrapped)
            {
                createPInvokeParams.Add($"int {param.Name}HasValue");
                createPInvokeParams.Add($"{param.InnerParameter!.CSharpPInvokeType} {param.Name}Value");
            }
            else
            {
                createPInvokeParams.Add($"{param.CSharpPInvokeType} {param.Name}");
            }
        }
        sb.AppendLine($"        internal static extern IntPtr Create({string.Join(", ", createPInvokeParams)});");
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

        // Session class
        sb.AppendLine($"    public sealed class {info.ViewName}Session : IDisposable");
        sb.AppendLine("    {");
        sb.AppendLine("        private IntPtr _handle;");
        sb.AppendLine("        private bool _disposed;");
        if (hasClosures)
        {
            sb.AppendLine("        private GCHandle[] _closureHandles = Array.Empty<GCHandle>();");
        }
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

        // Trampolines for closure parameters
        foreach (var param in bridgeParams.Where(p => p.Kind == BridgeParameterKind.VoidClosure))
        {
            var trampolineName = char.ToUpperInvariant(param.Name[0]) + param.Name[1..] + "Trampoline";
            sb.AppendLine($"        [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(CallConvCdecl) }})]");
            sb.AppendLine($"        private static void {trampolineName}(IntPtr userData)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (userData != IntPtr.Zero)");
            sb.AppendLine("            {");
            sb.AppendLine("                var h = GCHandle.FromIntPtr(userData);");
            sb.AppendLine("                if (h.Target is Action action)");
            sb.AppendLine("                    action();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // Create factory method
        EmitSimpleCreateFactory(sb, info, bridgeParams, needsUnsafe, hasClosures, hasStrings);

        sb.AppendLine("        public void Dispose()");
        sb.AppendLine("        {");
        sb.AppendLine("            if (!_disposed)");
        sb.AppendLine("            {");
        sb.AppendLine("                _disposed = true;");
        sb.AppendLine($"                {info.ViewName}BridgeNativeMethods.Free(_handle);");
        if (hasClosures)
        {
            sb.AppendLine("                foreach (var h in _closureHandles)");
            sb.AppendLine("                    if (h.IsAllocated) h.Free();");
            sb.AppendLine("                _closureHandles = Array.Empty<GCHandle>();");
        }
        sb.AppendLine("                _handle = IntPtr.Zero;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitSimpleCreateFactory(
        StringBuilder sb, ViewBridgeInfo info, List<BridgeParameter> bridgeParams,
        bool needsUnsafe, bool hasClosures, bool hasStrings)
    {
        // Factory parameter list (idiomatic C# types)
        var factoryParams = new List<string>();
        foreach (var param in bridgeParams)
        {
            var type = GetFactoryParamType(param);
            var defaultVal = param.Kind == BridgeParameterKind.VoidClosure ? " = null"
                : param.Kind == BridgeParameterKind.String ? " = null" : "";
            factoryParams.Add($"{type} {param.Name}{defaultVal}");
        }

        var unsafeKeyword = needsUnsafe ? "unsafe " : "";
        sb.AppendLine($"        public static {unsafeKeyword}{info.ViewName}Session Create({string.Join(", ", factoryParams)})");
        sb.AppendLine("        {");

        if (hasClosures)
        {
            sb.AppendLine("            var closureHandles = new System.Collections.Generic.List<GCHandle>();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            var indent = "                ";

            // Setup closure parameters
            foreach (var param in bridgeParams.Where(p => p.Kind == BridgeParameterKind.VoidClosure))
            {
                var trampolineName = char.ToUpperInvariant(param.Name[0]) + param.Name[1..] + "Trampoline";
                sb.AppendLine($"{indent}IntPtr {param.Name}Callback = IntPtr.Zero;");
                sb.AppendLine($"{indent}IntPtr {param.Name}UserData = IntPtr.Zero;");
                sb.AppendLine($"{indent}if ({param.Name} != null)");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    var h = GCHandle.Alloc({param.Name});");
                sb.AppendLine($"{indent}    closureHandles.Add(h);");
                sb.AppendLine($"{indent}    {param.Name}UserData = GCHandle.ToIntPtr(h);");
                sb.AppendLine($"{indent}    delegate* unmanaged[Cdecl]<IntPtr, void> fn = &{trampolineName};");
                sb.AppendLine($"{indent}    {param.Name}Callback = (IntPtr)fn;");
                sb.AppendLine($"{indent}}}");
                sb.AppendLine();
            }

            // String encoding
            foreach (var param in bridgeParams.Where(p => p.Kind == BridgeParameterKind.String))
            {
                sb.AppendLine($"{indent}var {param.Name}Bytes = Encoding.UTF8.GetBytes({param.Name} ?? \"\");");
            }

            // Call with fixed block if strings
            EmitSimpleCreateCall(sb, info, bridgeParams, hasStrings, hasClosures, indent);

            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                foreach (var h in closureHandles)");
            sb.AppendLine("                    if (h.IsAllocated) h.Free();");
            sb.AppendLine("            }");
        }
        else if (hasStrings)
        {
            var indent = "            ";
            // String encoding
            foreach (var param in bridgeParams.Where(p => p.Kind == BridgeParameterKind.String))
            {
                sb.AppendLine($"{indent}var {param.Name}Bytes = Encoding.UTF8.GetBytes({param.Name} ?? \"\");");
            }
            EmitSimpleCreateCall(sb, info, bridgeParams, hasStrings, hasClosures, indent);
        }
        else
        {
            // Simple case: no closures, no strings
            var nativeArgs = BuildSimpleNativeCallArgs(bridgeParams);
            sb.AppendLine($"            var handle = {info.ViewName}BridgeNativeMethods.Create({string.Join(", ", nativeArgs)});");
            sb.AppendLine("            if (handle == IntPtr.Zero)");
            sb.AppendLine($"                throw new InvalidOperationException(\"Failed to create {info.ViewName} session.\");");
            sb.AppendLine($"            return new {info.ViewName}Session(handle);");
        }

        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void EmitSimpleCreateCall(
        StringBuilder sb, ViewBridgeInfo info, List<BridgeParameter> bridgeParams,
        bool hasStrings, bool hasClosures, string indent)
    {
        var stringParams = bridgeParams.Where(p => p.Kind == BridgeParameterKind.String).ToList();
        var nativeArgs = BuildSimpleNativeCallArgs(bridgeParams);

        if (hasStrings)
        {
            var fixedDecls = string.Join(", ", stringParams.Select(p => $"byte* {p.Name}Ptr = {p.Name}Bytes"));
            sb.AppendLine($"{indent}fixed ({fixedDecls})");
            sb.AppendLine($"{indent}{{");
            var innerIndent = indent + "    ";
            sb.AppendLine($"{innerIndent}var handle = {info.ViewName}BridgeNativeMethods.Create({string.Join(", ", nativeArgs)});");
            sb.AppendLine($"{innerIndent}if (handle == IntPtr.Zero)");
            sb.AppendLine($"{innerIndent}    throw new InvalidOperationException(\"Failed to create {info.ViewName} session.\");");
            if (hasClosures)
            {
                sb.AppendLine($"{innerIndent}var session = new {info.ViewName}Session(handle);");
                sb.AppendLine($"{innerIndent}session._closureHandles = closureHandles.ToArray();");
                sb.AppendLine($"{innerIndent}closureHandles.Clear();");
                sb.AppendLine($"{innerIndent}return session;");
            }
            else
            {
                sb.AppendLine($"{innerIndent}return new {info.ViewName}Session(handle);");
            }
            sb.AppendLine($"{indent}}}");
        }
        else
        {
            sb.AppendLine($"{indent}var handle = {info.ViewName}BridgeNativeMethods.Create({string.Join(", ", nativeArgs)});");
            sb.AppendLine($"{indent}if (handle == IntPtr.Zero)");
            sb.AppendLine($"{indent}    throw new InvalidOperationException(\"Failed to create {info.ViewName} session.\");");
            if (hasClosures)
            {
                sb.AppendLine($"{indent}var session = new {info.ViewName}Session(handle);");
                sb.AppendLine($"{indent}session._closureHandles = closureHandles.ToArray();");
                sb.AppendLine($"{indent}closureHandles.Clear();");
                sb.AppendLine($"{indent}return session;");
            }
            else
            {
                sb.AppendLine($"{indent}return new {info.ViewName}Session(handle);");
            }
        }
    }

    private static List<string> BuildSimpleNativeCallArgs(List<BridgeParameter> bridgeParams)
    {
        var args = new List<string>();
        foreach (var param in bridgeParams)
        {
            if (param.Kind == BridgeParameterKind.VoidClosure)
            {
                args.Add($"{param.Name}Callback");
                args.Add($"{param.Name}UserData");
            }
            else if (param.Kind == BridgeParameterKind.String)
            {
                args.Add($"(IntPtr){param.Name}Ptr");
                args.Add($"{param.Name}Bytes.Length");
            }
            else if (param.Kind == BridgeParameterKind.BoundEnum)
            {
                args.Add($"({param.CSharpPInvokeType}){param.Name}");
            }
            else if (param.Kind == BridgeParameterKind.OptionalWrapped)
            {
                var inner = param.InnerParameter!;
                args.Add($"{param.Name}.HasValue ? 1 : 0");
                if (inner.Kind == BridgeParameterKind.BoundEnum)
                    args.Add($"{param.Name}.HasValue ? ({inner.CSharpPInvokeType}){param.Name}.Value : 0");
                else if (inner.CSharpConversion != null) // Bool
                    args.Add($"{param.Name}.HasValue ? ({param.Name}.Value {inner.CSharpConversion}) : 0");
                else
                    args.Add($"{param.Name} ?? 0");
            }
            else if (param.CSharpConversion != null)
            {
                // Bool: value ? 1 : 0
                args.Add($"{param.Name} {param.CSharpConversion}");
            }
            else
            {
                args.Add(param.Name);
            }
        }
        return args;
    }

    private static string GetFactoryParamType(BridgeParameter param) => param.Kind switch
    {
        BridgeParameterKind.VoidClosure => "Action?",
        BridgeParameterKind.String => "string?",
        BridgeParameterKind.Primitive when param.CSharpConversion != null => "bool",
        BridgeParameterKind.BoundEnum => param.CSharpTypeName!,
        BridgeParameterKind.OptionalWrapped => GetOptionalFactoryType(param),
        _ => param.CSharpPInvokeType,
    };

    private static string GetOptionalFactoryType(BridgeParameter param)
    {
        var inner = param.InnerParameter!;
        if (inner.Kind == BridgeParameterKind.BoundEnum)
            return $"{inner.CSharpTypeName}?";
        if (inner.CSharpConversion != null) // Bool
            return "bool?";
        return $"{inner.CSharpPInvokeType}?";
    }

    private static void EmitCSharpTemplate(StringBuilder sb, ViewBridgeInfo info)
    {
        var paramSummary = GetInitParamSummary(info);
        sb.AppendLine($"    // BRIDGE TEMPLATE: {info.ViewName} ({info.Classification}: {paramSummary})");
        sb.AppendLine($"    // See: src/docs/swiftui-bridge-design.md");
        sb.AppendLine();
    }

    #endregion

    #region Helpers

    private static string GetInitDescription(ViewBridgeInfo info)
    {
        if (info.Constructors.Count == 0)
            return "init()";

        var ctor = info.Constructors[0];
        var paramDescs = new List<string>();
        for (int i = 1; i < ctor.CSSignature.Count; i++)
        {
            var param = ctor.CSSignature[i];
            paramDescs.Add($"{param.Name}: {param.SwiftTypeSpec}");
        }

        return paramDescs.Count == 0 ? "init()" : $"init({string.Join(", ", paramDescs)})";
    }

    private static string GetInitParamSummary(ViewBridgeInfo info)
    {
        if (info.UnsupportedReason != null)
            return info.UnsupportedReason;

        if (info.Constructors.Count == 0)
            return "no parameters";

        var ctor = info.Constructors[0];
        var paramDescs = new List<string>();
        for (int i = 1; i < ctor.CSSignature.Count; i++)
        {
            var param = ctor.CSSignature[i];
            paramDescs.Add($"{param.Name}: {param.SwiftTypeSpec}");
        }

        return paramDescs.Count == 0 ? "no parameters" : string.Join(", ", paramDescs);
    }

    #endregion
}

/// <summary>
/// Classification of a View's init for bridge generation.
/// </summary>
public enum ViewInitClassification
{
    Simple,
    AsyncDependency,
    Unsupported,
}

/// <summary>
/// Information about a detected SwiftUI View for bridge generation.
/// </summary>
public record ViewBridgeInfo(
    string ViewName,
    string ModuleName,
    ViewInitClassification Classification,
    string? UnsupportedReason,
    List<MethodDecl> Constructors);
