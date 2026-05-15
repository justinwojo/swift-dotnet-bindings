// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text;

namespace BindingsGeneration;

/// <summary>
/// Lifecycle callbacks, universal SwiftUI modifiers, and presentation helpers.
/// All functional bridged views get these features via the always-present Wrapper pattern.
///
/// Lifecycle callbacks use the same pattern as universal modifiers: stored on the State
/// object and set via a post-Create @_cdecl function. This keeps the Create signature
/// unchanged and avoids breaking existing bridge consumers.
/// </summary>
public static partial class SwiftUIBridgeEmitter
{
    #region Lifecycle Callbacks — Swift

    /// <summary>
    /// Emits lifecycle callback vars on the State class (not @Published — no re-render needed).
    /// The Wrapper reads these via state reference in .onAppear/.onDisappear closures.
    /// </summary>
    internal static void EmitSwiftLifecycleStateVars(StringBuilder sb)
    {
        sb.AppendLine("    var lifecycleOnAppear: (() -> Void)? = nil");
        sb.AppendLine("    var lifecycleOnDisappear: (() -> Void)? = nil");
    }

    /// <summary>
    /// Emits .onAppear/.onDisappear modifier chain in the Wrapper body.
    /// Reads callbacks from the observed state object.
    /// </summary>
    internal static void EmitSwiftWrapperLifecycleModifiers(StringBuilder sb)
    {
        sb.AppendLine("            .onAppear { [state] in state.lifecycleOnAppear?() }");
        sb.AppendLine("            .onDisappear { [state] in state.lifecycleOnDisappear?() }");
    }

    /// <summary>
    /// Emits the Swift @_cdecl SetLifecycle function that stores lifecycle callbacks on the session state.
    /// Uses the same guard pattern as universal modifier Set functions.
    /// </summary>
    internal static void EmitSwiftLifecycleSetFunction(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        ModuleEmissionContext? emissionContext)
    {
        var funcName = $"{prefix}_SetLifecycle";
        // S5 audited (Tier C): SwiftUI lifecycle setter in `_direct_helper` bucket. funcName combines per-view `prefix` + fixed `_SetLifecycle` suffix — at most one per bridge.
        emissionContext?.TryAddDirectHelperWrapperSymbol(funcName);
        sb.AppendLine($"@_cdecl(\"{funcName}\")");
        sb.AppendLine($"public func {funcName}(");
        sb.AppendLine($"    _ handle: UnsafeMutableRawPointer?,");
        sb.AppendLine($"    _ onAppearCb: (@convention(c) (UnsafeMutableRawPointer?) -> Void)?,");
        sb.AppendLine($"    _ onAppearUd: UnsafeMutableRawPointer?,");
        sb.AppendLine($"    _ onDisappearCb: (@convention(c) (UnsafeMutableRawPointer?) -> Void)?,");
        sb.AppendLine($"    _ onDisappearUd: UnsafeMutableRawPointer?");
        sb.AppendLine(") {");
        sb.AppendLine("    SBW_onMainThread {");
        sb.AppendLine($"        guard let handle = handle,");
        sb.AppendLine($"              {handlesVar}.contains(handle) else {{ return }}");
        sb.AppendLine($"        let session = Unmanaged<{sessionClass}>");
        sb.AppendLine($"            .fromOpaque(handle).takeUnretainedValue()");
        sb.AppendLine($"        session.state.lifecycleOnAppear = onAppearCb != nil ? {{ onAppearCb!(onAppearUd) }} : nil");
        sb.AppendLine($"        session.state.lifecycleOnDisappear = onDisappearCb != nil ? {{ onDisappearCb!(onDisappearUd) }} : nil");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    #endregion

    #region Universal Modifiers — Swift

    /// <summary>
    /// Emits @Published state vars for the curated set of universal SwiftUI modifiers.
    /// </summary>
    internal static void EmitSwiftUniversalModifierStateVars(StringBuilder sb)
    {
        sb.AppendLine("    @Published var u_frameWidth: CGFloat? = nil");
        sb.AppendLine("    @Published var u_frameHeight: CGFloat? = nil");
        sb.AppendLine("    @Published var u_padding: CGFloat? = nil");
        sb.AppendLine("    @Published var u_backgroundColor: SwiftUI.Color? = nil");
        sb.AppendLine("    @Published var u_foregroundColor: SwiftUI.Color? = nil");
        sb.AppendLine("    @Published var u_cornerRadius: CGFloat? = nil");
        sb.AppendLine("    @Published var u_opacity: Double? = nil");
        sb.AppendLine("    @Published var u_font: SwiftUI.Font? = nil");
    }

    /// <summary>
    /// Emits the applyUniversalModifiers helper on the Wrapper view.
    /// Uses AnyView type erasure — acceptable for bridge views (one hosting controller per view).
    /// </summary>
    internal static void EmitSwiftUniversalModifierHelper(StringBuilder sb)
    {
        sb.AppendLine("    private func applyUniversalModifiers<V: View>(_ view: V) -> AnyView {");
        sb.AppendLine("        var v = AnyView(view)");
        sb.AppendLine("        if state.u_frameWidth != nil || state.u_frameHeight != nil {");
        sb.AppendLine("            v = AnyView(v.frame(width: state.u_frameWidth, height: state.u_frameHeight))");
        sb.AppendLine("        }");
        sb.AppendLine("        if let p = state.u_padding { v = AnyView(v.padding(p)) }");
        // Explicit SwiftUI.Color? type annotation avoids:
        // 1. @ObservedObject subscript(dynamicMember:) ambiguity (Color conforms to View)
        // 2. Name collision with user-defined Color types in the target module
        sb.AppendLine("        let bgColor: SwiftUI.Color? = state.u_backgroundColor");
        sb.AppendLine("        if let bg = bgColor { v = AnyView(v.background(bg)) }");
        sb.AppendLine("        let fgColor: SwiftUI.Color? = state.u_foregroundColor");
        sb.AppendLine("        if let fg = fgColor { v = AnyView(v.foregroundColor(fg)) }");
        sb.AppendLine("        if let cr = state.u_cornerRadius { v = AnyView(v.cornerRadius(cr)) }");
        sb.AppendLine("        if let op = state.u_opacity { v = AnyView(v.opacity(op)) }");
        sb.AppendLine("        if let f = state.u_font { v = AnyView(v.font(f)) }");
        sb.AppendLine("        return v");
        sb.AppendLine("    }");
    }

    /// <summary>
    /// Emits Swift @_cdecl Set functions for each universal modifier.
    /// </summary>
    internal static void EmitSwiftUniversalModifierSetFunctions(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        HashSet<string>? viewModifierNames,
        ModuleEmissionContext? emissionContext)
    {
        EmitUniversalSetFunction(sb, prefix, sessionClass, handlesVar, viewModifierNames, emissionContext,
            "SetFrame",
            new[] { "_ hasWidth: Int32", "_ width: Double", "_ hasHeight: Int32", "_ height: Double" },
            new[] {
                "        session.state.u_frameWidth = hasWidth != 0 ? CGFloat(width) : nil",
                "        session.state.u_frameHeight = hasHeight != 0 ? CGFloat(height) : nil",
            });

        EmitUniversalSetFunction(sb, prefix, sessionClass, handlesVar, viewModifierNames, emissionContext,
            "SetPadding",
            new[] { "_ hasValue: Int32", "_ value: Double" },
            new[] { "        session.state.u_padding = hasValue != 0 ? CGFloat(value) : nil" });

        EmitUniversalSetFunction(sb, prefix, sessionClass, handlesVar, viewModifierNames, emissionContext,
            "SetBackground",
            new[] { "_ hasValue: Int32", "_ r: Double", "_ g: Double", "_ b: Double", "_ a: Double" },
            new[] { "        session.state.u_backgroundColor = hasValue != 0 ? SwiftUI.Color(red: r, green: g, blue: b, opacity: a) : nil" });

        EmitUniversalSetFunction(sb, prefix, sessionClass, handlesVar, viewModifierNames, emissionContext,
            "SetForegroundColor",
            new[] { "_ hasValue: Int32", "_ r: Double", "_ g: Double", "_ b: Double", "_ a: Double" },
            new[] { "        session.state.u_foregroundColor = hasValue != 0 ? SwiftUI.Color(red: r, green: g, blue: b, opacity: a) : nil" });

        EmitUniversalSetFunction(sb, prefix, sessionClass, handlesVar, viewModifierNames, emissionContext,
            "SetCornerRadius",
            new[] { "_ hasValue: Int32", "_ value: Double" },
            new[] { "        session.state.u_cornerRadius = hasValue != 0 ? CGFloat(value) : nil" });

        EmitUniversalSetFunction(sb, prefix, sessionClass, handlesVar, viewModifierNames, emissionContext,
            "SetOpacity",
            new[] { "_ hasValue: Int32", "_ value: Double" },
            new[] { "        session.state.u_opacity = hasValue != 0 ? value : nil" });

        EmitUniversalSetFunction(sb, prefix, sessionClass, handlesVar, viewModifierNames, emissionContext,
            "SetFont",
            new[] { "_ hasValue: Int32", "_ size: Double" },
            new[] { "        session.state.u_font = hasValue != 0 ? SwiftUI.Font.system(size: CGFloat(size)) : nil" });
    }

    private static void EmitUniversalSetFunction(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        HashSet<string>? viewModifierNames,
        ModuleEmissionContext? emissionContext,
        string functionSuffix, string[] extraParams, string[] bodyLines)
    {
        // Skip if a view-specific modifier already emits a Set function with the same name
        if (viewModifierNames != null && viewModifierNames.Contains(functionSuffix))
            return;

        var funcName = $"{prefix}_{functionSuffix}";
        // S5 audited (Tier C): SwiftUI universal-modifier setter in `_direct_helper` bucket. funcName combines per-view `prefix` + per-modifier suffix; view-specific overrides skip via viewModifierNames check above.
        emissionContext?.TryAddDirectHelperWrapperSymbol(funcName);
        sb.AppendLine($"@_cdecl(\"{funcName}\")");

        var allParams = new List<string> { "_ handle: UnsafeMutableRawPointer?" };
        allParams.AddRange(extraParams);

        sb.AppendLine($"public func {funcName}(");
        sb.AppendLine($"    {string.Join(",\n    ", allParams)}");
        sb.AppendLine(") {");
        sb.AppendLine("    SBW_onMainThread {");
        sb.AppendLine($"        guard let handle = handle,");
        sb.AppendLine($"              {handlesVar}.contains(handle) else {{ return }}");
        sb.AppendLine($"        let session = Unmanaged<{sessionClass}>");
        sb.AppendLine($"            .fromOpaque(handle).takeUnretainedValue()");
        foreach (var line in bodyLines)
            sb.AppendLine(line);
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    #endregion

    #region Presentation Helpers — Swift

    /// <summary>
    /// Emits @_cdecl presentation functions: PresentAsSheet, PushOnNav, Dismiss.
    /// </summary>
    internal static void EmitSwiftPresentationFunctions(
        StringBuilder sb, string prefix, string sessionClass, string handlesVar,
        ModuleEmissionContext? emissionContext)
    {
        // PresentAsSheet
        var funcName = $"{prefix}_PresentAsSheet";
        // S5 audited (Tier C): SwiftUI presentation helpers in `_direct_helper` bucket. Three fixed-suffix funcs per view (`_PresentAsSheet`/`_PushOnNav`/`_Dismiss`); per-view `prefix` makes them globally unique.
        emissionContext?.TryAddDirectHelperWrapperSymbol(funcName);
        sb.AppendLine($"@_cdecl(\"{funcName}\")");
        sb.AppendLine($"public func {funcName}(_ handle: UnsafeMutableRawPointer?, _ fromVC: UnsafeMutableRawPointer?) {{");
        sb.AppendLine("    SBW_onMainThread {");
        sb.AppendLine($"        guard let handle = handle, let fromVC = fromVC,");
        sb.AppendLine($"              {handlesVar}.contains(handle) else {{ return }}");
        sb.AppendLine($"        let session = Unmanaged<{sessionClass}>.fromOpaque(handle).takeUnretainedValue()");
        sb.AppendLine($"        let parent = Unmanaged<UIViewController>.fromOpaque(fromVC).takeUnretainedValue()");
        sb.AppendLine($"        parent.present(session.hostingController, animated: true)");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        // PushOnNavigationStack
        funcName = $"{prefix}_PushOnNav";
        // S5 audited (Tier C): see PresentAsSheet above — same `_direct_helper` bucket, per-view `prefix` + fixed suffix.
        emissionContext?.TryAddDirectHelperWrapperSymbol(funcName);
        sb.AppendLine($"@_cdecl(\"{funcName}\")");
        sb.AppendLine($"public func {funcName}(_ handle: UnsafeMutableRawPointer?, _ navVC: UnsafeMutableRawPointer?) {{");
        sb.AppendLine("    SBW_onMainThread {");
        sb.AppendLine($"        guard let handle = handle, let navVC = navVC,");
        sb.AppendLine($"              {handlesVar}.contains(handle) else {{ return }}");
        sb.AppendLine($"        let session = Unmanaged<{sessionClass}>.fromOpaque(handle).takeUnretainedValue()");
        sb.AppendLine($"        let nav = Unmanaged<UINavigationController>.fromOpaque(navVC).takeUnretainedValue()");
        sb.AppendLine($"        nav.pushViewController(session.hostingController, animated: true)");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        // Dismiss
        funcName = $"{prefix}_Dismiss";
        // S5 audited (Tier C): see PresentAsSheet above — same `_direct_helper` bucket, per-view `prefix` + fixed suffix.
        emissionContext?.TryAddDirectHelperWrapperSymbol(funcName);
        sb.AppendLine($"@_cdecl(\"{funcName}\")");
        sb.AppendLine($"public func {funcName}(_ handle: UnsafeMutableRawPointer?) {{");
        sb.AppendLine("    SBW_onMainThread {");
        sb.AppendLine($"        guard let handle = handle,");
        sb.AppendLine($"              {handlesVar}.contains(handle) else {{ return }}");
        sb.AppendLine($"        let session = Unmanaged<{sessionClass}>.fromOpaque(handle).takeUnretainedValue()");
        sb.AppendLine($"        session.hostingController.dismiss(animated: true)");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    #endregion

    #region Lifecycle Callbacks — C#

    /// <summary>
    /// Emits C# [UnmanagedCallersOnly] trampolines for onAppear/onDisappear.
    /// </summary>
    internal static void EmitCSharpLifecycleTrampolines(StringBuilder sb)
    {
        sb.AppendLine($"        [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(global::System.Runtime.CompilerServices.CallConvCdecl) }})]");
        sb.AppendLine($"        private static void OnAppearTrampoline(IntPtr userData)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (userData != IntPtr.Zero)");
        sb.AppendLine("            {");
        sb.AppendLine("                var h = GCHandle.FromIntPtr(userData);");
        sb.AppendLine("                if (h.Target is Action action)");
        sb.AppendLine("                    action();");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine($"        [UnmanagedCallersOnly(CallConvs = new[] {{ typeof(global::System.Runtime.CompilerServices.CallConvCdecl) }})]");
        sb.AppendLine($"        private static void OnDisappearTrampoline(IntPtr userData)");
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

    /// <summary>
    /// Emits C# P/Invoke for SetLifecycle and C# lifecycle setup method.
    /// </summary>
    internal static void EmitCSharpLifecyclePInvoke(
        StringBuilder sb, string prefix, string bridgeLib,
        ModuleEmissionContext? emissionContext)
    {
        EmitSimplePInvoke(sb, bridgeLib, $"{prefix}_SetLifecycle", "SetLifecycle",
            "IntPtr handle, IntPtr onAppearCb, IntPtr onAppearUd, IntPtr onDisappearCb, IntPtr onDisappearUd",
            emissionContext);
    }

    /// <summary>
    /// Emits the C# SetLifecycle method on the Session class.
    /// Called internally by Create factory when onAppear/onDisappear are provided.
    /// Allocates GCHandles and stores them in _lifecycleHandles.
    /// </summary>
    internal static void EmitCSharpLifecycleMethod(
        StringBuilder sb, ViewBridgeInfo info)
    {
        var nm = $"{info.ViewName}BridgeNativeMethods";

        sb.AppendLine($"        private unsafe void SetLifecycleCallbacks(Action? onAppear, Action? onDisappear)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (onAppear == null && onDisappear == null) return;");
        sb.AppendLine("            foreach (var h in _lifecycleHandles) if (h.IsAllocated) h.Free();");
        sb.AppendLine("            var handles = new global::System.Collections.Generic.List<GCHandle>();");
        sb.AppendLine("            IntPtr onAppearCb = IntPtr.Zero, onAppearUd = IntPtr.Zero;");
        sb.AppendLine("            if (onAppear != null)");
        sb.AppendLine("            {");
        sb.AppendLine("                var h = GCHandle.Alloc(onAppear);");
        sb.AppendLine("                handles.Add(h);");
        sb.AppendLine("                onAppearUd = GCHandle.ToIntPtr(h);");
        sb.AppendLine("                delegate* unmanaged[Cdecl]<IntPtr, void> fn = &OnAppearTrampoline;");
        sb.AppendLine("                onAppearCb = (IntPtr)fn;");
        sb.AppendLine("            }");
        sb.AppendLine("            IntPtr onDisappearCb = IntPtr.Zero, onDisappearUd = IntPtr.Zero;");
        sb.AppendLine("            if (onDisappear != null)");
        sb.AppendLine("            {");
        sb.AppendLine("                var h = GCHandle.Alloc(onDisappear);");
        sb.AppendLine("                handles.Add(h);");
        sb.AppendLine("                onDisappearUd = GCHandle.ToIntPtr(h);");
        sb.AppendLine("                delegate* unmanaged[Cdecl]<IntPtr, void> fn = &OnDisappearTrampoline;");
        sb.AppendLine("                onDisappearCb = (IntPtr)fn;");
        sb.AppendLine("            }");
        sb.AppendLine("            _lifecycleHandles = handles.ToArray();");
        sb.AppendLine($"            {nm}.SetLifecycle(Handle, onAppearCb, onAppearUd, onDisappearCb, onDisappearUd);");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    #endregion

    #region Universal Modifiers — C#

    /// <summary>
    /// Emits C# P/Invoke declarations for universal modifier Set functions.
    /// </summary>
    internal static void EmitCSharpUniversalModifierPInvokes(
        StringBuilder sb, string prefix, string bridgeLib,
        HashSet<string>? viewModifierNames,
        ModuleEmissionContext? emissionContext)
    {
        EmitUniversalPInvokeIfNotSkipped(sb, bridgeLib, prefix, viewModifierNames,
            "SetFrame", "IntPtr handle, int hasWidth, double width, int hasHeight, double height", emissionContext);

        EmitUniversalPInvokeIfNotSkipped(sb, bridgeLib, prefix, viewModifierNames,
            "SetPadding", "IntPtr handle, int hasValue, double value", emissionContext);

        EmitUniversalPInvokeIfNotSkipped(sb, bridgeLib, prefix, viewModifierNames,
            "SetBackground", "IntPtr handle, int hasValue, double r, double g, double b, double a", emissionContext);

        EmitUniversalPInvokeIfNotSkipped(sb, bridgeLib, prefix, viewModifierNames,
            "SetForegroundColor", "IntPtr handle, int hasValue, double r, double g, double b, double a", emissionContext);

        EmitUniversalPInvokeIfNotSkipped(sb, bridgeLib, prefix, viewModifierNames,
            "SetCornerRadius", "IntPtr handle, int hasValue, double value", emissionContext);

        EmitUniversalPInvokeIfNotSkipped(sb, bridgeLib, prefix, viewModifierNames,
            "SetOpacity", "IntPtr handle, int hasValue, double value", emissionContext);

        EmitUniversalPInvokeIfNotSkipped(sb, bridgeLib, prefix, viewModifierNames,
            "SetFont", "IntPtr handle, int hasValue, double size", emissionContext);
    }

    private static void EmitUniversalPInvokeIfNotSkipped(
        StringBuilder sb, string bridgeLib, string prefix,
        HashSet<string>? viewModifierNames, string functionSuffix, string parameters,
        ModuleEmissionContext? emissionContext)
    {
        if (viewModifierNames != null && viewModifierNames.Contains(functionSuffix))
            return;

        EmitSimplePInvoke(sb, bridgeLib, $"{prefix}_{functionSuffix}", functionSuffix, parameters, emissionContext);
    }

    /// <summary>
    /// Emits C# public methods on the Session class for universal modifiers.
    /// </summary>
    internal static void EmitCSharpUniversalModifierMethods(
        StringBuilder sb, ViewBridgeInfo info,
        HashSet<string>? viewModifierNames = null)
    {
        var nm = $"{info.ViewName}BridgeNativeMethods";
        bool skip(string name) => viewModifierNames != null && viewModifierNames.Contains(name);

        if (!skip("SetFrame"))
        {
            sb.AppendLine($"        public void SetFrame(double? width = null, double? height = null) =>");
            sb.AppendLine($"            {nm}.SetFrame(Handle, width.HasValue ? 1 : 0, width ?? 0, height.HasValue ? 1 : 0, height ?? 0);");
            sb.AppendLine();
        }

        if (!skip("SetPadding"))
        {
            sb.AppendLine($"        public void SetPadding(double? value) =>");
            sb.AppendLine($"            {nm}.SetPadding(Handle, value.HasValue ? 1 : 0, value ?? 0);");
            sb.AppendLine();
        }

        if (!skip("SetBackground"))
        {
            sb.AppendLine($"        public void SetBackground(double r, double g, double b, double a = 1.0) =>");
            sb.AppendLine($"            {nm}.SetBackground(Handle, 1, r, g, b, a);");
            sb.AppendLine();

            sb.AppendLine($"        public void ClearBackground() =>");
            sb.AppendLine($"            {nm}.SetBackground(Handle, 0, 0, 0, 0, 0);");
            sb.AppendLine();
        }

        if (!skip("SetForegroundColor"))
        {
            sb.AppendLine($"        public void SetForegroundColor(double r, double g, double b, double a = 1.0) =>");
            sb.AppendLine($"            {nm}.SetForegroundColor(Handle, 1, r, g, b, a);");
            sb.AppendLine();

            sb.AppendLine($"        public void ClearForegroundColor() =>");
            sb.AppendLine($"            {nm}.SetForegroundColor(Handle, 0, 0, 0, 0, 0);");
            sb.AppendLine();
        }

        if (!skip("SetCornerRadius"))
        {
            sb.AppendLine($"        public void SetCornerRadius(double? value) =>");
            sb.AppendLine($"            {nm}.SetCornerRadius(Handle, value.HasValue ? 1 : 0, value ?? 0);");
            sb.AppendLine();
        }

        if (!skip("SetOpacity"))
        {
            sb.AppendLine($"        public void SetOpacity(double? value) =>");
            sb.AppendLine($"            {nm}.SetOpacity(Handle, value.HasValue ? 1 : 0, value ?? 0);");
            sb.AppendLine();
        }

        if (!skip("SetFont"))
        {
            sb.AppendLine($"        public void SetFontSize(double? size) =>");
            sb.AppendLine($"            {nm}.SetFont(Handle, size.HasValue ? 1 : 0, size ?? 0);");
            sb.AppendLine();
        }
    }

    #endregion

    #region Presentation Helpers — C#

    /// <summary>
    /// Emits C# P/Invoke declarations for presentation functions.
    /// </summary>
    internal static void EmitCSharpPresentationPInvokes(
        StringBuilder sb, string prefix, string bridgeLib,
        ModuleEmissionContext? emissionContext)
    {
        EmitSimplePInvoke(sb, bridgeLib, $"{prefix}_PresentAsSheet", "PresentAsSheet",
            "IntPtr handle, IntPtr fromViewController", emissionContext);
        EmitSimplePInvoke(sb, bridgeLib, $"{prefix}_PushOnNav", "PushOnNav",
            "IntPtr handle, IntPtr navigationController", emissionContext);
        EmitSimplePInvoke(sb, bridgeLib, $"{prefix}_Dismiss", "Dismiss",
            "IntPtr handle", emissionContext);
    }

    /// <summary>
    /// Emits C# public methods on the Session class for presentation.
    /// </summary>
    internal static void EmitCSharpPresentationMethods(
        StringBuilder sb, ViewBridgeInfo info)
    {
        var nm = $"{info.ViewName}BridgeNativeMethods";

        sb.AppendLine($"        public void PresentAsSheet(IntPtr fromViewController) =>");
        sb.AppendLine($"            {nm}.PresentAsSheet(Handle, fromViewController);");
        sb.AppendLine();

        sb.AppendLine($"        public void PushOnNavigationStack(IntPtr navigationController) =>");
        sb.AppendLine($"            {nm}.PushOnNav(Handle, navigationController);");
        sb.AppendLine();

        sb.AppendLine($"        public void Dismiss() =>");
        sb.AppendLine($"            {nm}.Dismiss(Handle);");
        sb.AppendLine();
    }

    #endregion

    #region Shared P/Invoke Helper

    private static void EmitSimplePInvoke(
        StringBuilder sb, string bridgeLib, string entryPoint, string methodName, string parameters,
        ModuleEmissionContext? emissionContext)
    {
        sb.AppendLine();
        foreach (var line in PInvokeEmitHelper.FormatDeclarationLines(new PInvokeEmissionInfo
        {
            LibraryPath = bridgeLib,
            EntryPoint = entryPoint,
            MethodName = methodName,
            ReturnType = "void",
            ParametersString = parameters,
            CallingConvention = PInvokeCallingConvention.Cdecl,
            Visibility = PInvokeVisibility.Internal,
            EmissionContext = emissionContext,
            EnforceWrapperContract = true
        }))
            sb.AppendLine($"        {line}");
    }

    #endregion
}
