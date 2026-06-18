// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Single source of truth for rendering an <c>@_cdecl</c> wrapper's <c>return</c> statement(s)
/// from a <see cref="CdeclReturnMapping"/>. Collapses the six historically hand-copied
/// per-<see cref="CdeclReturnKind"/> switches (method direct return, method generic-extension
/// body lines, method throwing-error sentinel, property getter, property generic-parent body
/// lines, subscript direct return) into one place so the ClassPointer retain semantics, the
/// Bool→Int8 ternary, and the tag-only-enum widening copy each have exactly one home.
///
/// The renderer reproduces each historical call site byte-for-byte. Three call shapes exist and
/// are intentionally preserved (their differences are semantically inert Swift formatting that
/// must not change under a behavior-preserving refactor):
///   * <see cref="Write"/> / <see cref="Lines"/> — inline form: the value expression is spliced
///     directly into the conversion. <paramref name="scalarParens"/> selects whether scalar
///     conversions wrap the value as <c>(expr)</c> (method/subscript direct returns) or splice it
///     bare (property getters, whose access expression is already a simple member access).
///   * <see cref="LinesBindingResult"/> — binds <c>let result = callExpr</c> once and returns
///     <c>result</c> (the generic-extension method body, where the call must be evaluated once).
///   * <see cref="WriteErrorSentinel"/> — the throwing-wrapper catch-block dummy return.
/// </summary>
internal static class CdeclReturnRenderer
{
    /// <summary>
    /// Inline writer form: emits <c>return &lt;conversion of valueExpr&gt;</c> directly.
    /// Used by the method and subscript direct-return emitters (<paramref name="scalarParens"/> =
    /// <c>true</c>) and the property getter (<paramref name="scalarParens"/> = <c>false</c>).
    /// </summary>
    internal static void Write(SwiftWriter swiftWriter, string valueExpr, TypeSpec typeSpec,
        ITypeDatabase typeDatabase, CdeclReturnMapping mapping, bool scalarParens)
    {
        foreach (var line in BuildInlineLines(valueExpr, typeSpec, typeDatabase, mapping, scalarParens))
            swiftWriter.WriteLine(line);
    }

    /// <summary>
    /// Inline lines form: the same statements as <see cref="Write"/>, returned as a list for
    /// emitters that collect body lines before post-processing (the property generic-parent body).
    /// </summary>
    internal static List<string> Lines(string valueExpr, TypeSpec typeSpec,
        ITypeDatabase typeDatabase, CdeclReturnMapping mapping, bool scalarParens)
        => BuildInlineLines(valueExpr, typeSpec, typeDatabase, mapping, scalarParens);

    /// <summary>
    /// Result-bound lines form: binds <c>let result = callExpr</c> and returns <c>result</c>.
    /// Used by the generic-extension method body, where <paramref name="callExpr"/> is a method
    /// invocation that must be evaluated exactly once.
    /// </summary>
    internal static List<string> LinesBindingResult(string callExpr, TypeSpec typeSpec,
        ITypeDatabase typeDatabase, CdeclReturnMapping mapping)
    {
        switch (mapping.Kind)
        {
            case CdeclReturnKind.Bool:
                return new List<string> { $"let result = {callExpr}", "return result ? 1 : 0" };

            case CdeclReturnKind.SimpleEnum:
                if (HasRawValue(typeSpec, typeDatabase))
                    return new List<string>
                    {
                        $"let result = {callExpr}",
                        $"return {mapping.CdeclReturnType}(result.rawValue)",
                    };
                // Tag-only enum: zero-initialize and copyMemory to avoid reading past
                // the enum's 1-byte allocation (load(as: Int.self) reads 8 bytes → crash).
                return WrapperEmitterHelpers.GetTagOnlyEnumReturnLines(callExpr, mapping.CdeclReturnType);

            case CdeclReturnKind.ClassPointer:
                return new List<string>
                {
                    $"let result = {callExpr}",
                    "return Unmanaged.passRetained(result as AnyObject).toOpaque()",
                };

            case CdeclReturnKind.OptionalClassPointer:
                return new List<string>
                {
                    $"let result = {callExpr}",
                    "return result.map { Unmanaged.passRetained($0 as AnyObject).toOpaque() }",
                };

            case CdeclReturnKind.Direct:
            default:
                return new List<string> { $"return {callExpr}" };
        }
    }

    /// <summary>
    /// Emits the dummy sentinel return for a throwing wrapper's catch block, when the function has
    /// a non-void direct return (not routed through a result pointer). The sentinel value is never
    /// observed by managed code (the error path is taken) — it only satisfies <c>swiftc</c>'s
    /// requirement that every path return a value of the declared cdecl return type.
    /// </summary>
    internal static void WriteErrorSentinel(SwiftWriter swiftWriter, CdeclReturnMapping mapping)
    {
        switch (mapping.Kind)
        {
            case CdeclReturnKind.Bool:
                swiftWriter.WriteLine("    return 0");
                break;
            case CdeclReturnKind.SimpleEnum:
                swiftWriter.WriteLine("    return 0");
                break;
            case CdeclReturnKind.ClassPointer:
                swiftWriter.WriteLine("    return UnsafeMutableRawPointer(bitPattern: 1)!");
                break;
            case CdeclReturnKind.OptionalClassPointer:
                swiftWriter.WriteLine("    return nil");
                break;
            case CdeclReturnKind.Direct:
                swiftWriter.WriteLine("    return 0");
                break;
        }
    }

    private static List<string> BuildInlineLines(string valueExpr, TypeSpec typeSpec,
        ITypeDatabase typeDatabase, CdeclReturnMapping mapping, bool scalarParens)
    {
        // scalarParens controls whether scalar conversions (Bool ternary, simple-enum rawValue)
        // wrap the value as (expr). ClassPointer/Optional/Direct never depend on it: ClassPointer
        // and Direct always splice bare, Optional always parenthesizes for the `.map` receiver.
        string scalar = scalarParens ? $"({valueExpr})" : valueExpr;
        switch (mapping.Kind)
        {
            case CdeclReturnKind.Bool:
                return new List<string> { $"return {scalar} ? 1 : 0" };

            case CdeclReturnKind.SimpleEnum:
                if (HasRawValue(typeSpec, typeDatabase))
                    return new List<string> { $"return {mapping.CdeclReturnType}({scalar}.rawValue)" };
                // Tag-only enum: zero-initialize and copyMemory to avoid reading past
                // the enum's 1-byte allocation (load(as: Int.self) reads 8 bytes → crash).
                return WrapperEmitterHelpers.GetTagOnlyEnumReturnLines(valueExpr, mapping.CdeclReturnType);

            case CdeclReturnKind.ClassPointer:
                // Use `as AnyObject` for safety — handles both true classes and ObjC-bridged structs.
                // Unmanaged.passRetained requires T: AnyObject; ObjC-bridged structs (e.g., IndexPath)
                // need the bridge cast. For true classes, `as AnyObject` is a no-op upcast.
                return new List<string> { $"return Unmanaged.passRetained({valueExpr} as AnyObject).toOpaque()" };

            case CdeclReturnKind.OptionalClassPointer:
                // Use `as AnyObject` in the .map closure — ObjC-bridged structs (e.g., NSZone,
                // IndexPath) are Swift structs and Unmanaged<T> requires T: AnyObject.
                return new List<string> { $"return ({valueExpr}).map {{ Unmanaged.passRetained($0 as AnyObject).toOpaque() }}" };

            case CdeclReturnKind.Direct:
            default:
                return new List<string> { $"return {valueExpr}" };
        }
    }

    private static bool HasRawValue(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => typeDatabase.TryGetTypeRecord(typeSpec, out var enumRecord)
           && !string.IsNullOrEmpty(enumRecord.RawValueTypeName);
}
