// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Calling convention for P/Invoke declarations.
/// </summary>
public enum PInvokeCallingConvention
{
    /// <summary>Swift calling convention via CallConvSwift.</summary>
    Swift,
    /// <summary>C calling convention via CallConvCdecl.</summary>
    Cdecl
}

/// <summary>
/// Visibility for P/Invoke declarations.
/// </summary>
public enum PInvokeVisibility
{
    Private,
    Internal,
    Public
}

/// <summary>
/// Describes a P/Invoke declaration for centralized emission.
/// </summary>
public record PInvokeEmissionInfo
{
    /// <summary>The library path for LibraryImport.</summary>
    public required string LibraryPath { get; init; }

    /// <summary>The entry point (mangled Swift symbol name).</summary>
    public required string EntryPoint { get; init; }

    /// <summary>The P/Invoke method name.</summary>
    public required string MethodName { get; init; }

    /// <summary>The return type of the P/Invoke method.</summary>
    public required string ReturnType { get; init; }

    /// <summary>The parameter list string for the P/Invoke method.</summary>
    public required string ParametersString { get; init; }

    /// <summary>Calling convention (Swift or Cdecl).</summary>
    public PInvokeCallingConvention CallingConvention { get; init; } = PInvokeCallingConvention.Swift;

    /// <summary>Visibility modifier for the declaration.</summary>
    public PInvokeVisibility Visibility { get; init; } = PInvokeVisibility.Private;

    /// <summary>Whether to emit the 'new' modifier (for derived class metadata accessors).</summary>
    public bool HasNewModifier { get; init; }

    /// <summary>Whether to use fully-qualified type names (for marker protocol overloads).</summary>
    public bool UseFullyQualifiedNames { get; init; }

    /// <summary>Additional TypeMetadata parameters for generic type support.</summary>
    public IReadOnlyList<string>? MetadataParameters { get; init; }

    /// <summary>Whether this P/Invoke is for an async method (always returns void).</summary>
    public bool IsAsync { get; init; }

    /// <summary>Whether the method needs the 'unsafe' modifier.</summary>
    public bool IsUnsafe { get; init; }
}

/// <summary>
/// Centralized helper for emitting P/Invoke declarations.
/// Eliminates duplicated emission logic across 19 emitter files.
/// </summary>
public static class PInvokeEmitHelper
{
    /// <summary>
    /// Emit a P/Invoke declaration to a CSharpWriter.
    /// </summary>
    public static void EmitDeclaration(CSharpWriter csWriter, PInvokeEmissionInfo info)
    {
        foreach (var line in FormatDeclarationLines(info))
        {
            csWriter.WriteLine(line);
        }
    }

    /// <summary>
    /// Format a P/Invoke declaration as individual lines (unindented).
    /// Callers prepend their own indentation when appending to StringBuilder or raw strings.
    /// </summary>
    public static IReadOnlyList<string> FormatDeclarationLines(PInvokeEmissionInfo info)
    {
        var lines = new List<string>();

        // Calling convention attribute
        var callConvType = info.CallingConvention == PInvokeCallingConvention.Swift
            ? "CallConvSwift"
            : "CallConvCdecl";

        // If any parameter or return type is string, add StringMarshalling attribute
        var needsStringMarshalling = info.ParametersString.Contains("string ")
            || info.ParametersString.Contains("string?")
            || (!info.IsAsync && info.ReturnType == "string");
        var stringMarshalSuffix = needsStringMarshalling ? ", StringMarshalling = StringMarshalling.Utf8" : "";

        if (info.UseFullyQualifiedNames)
        {
            lines.Add($"[global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new global::System.Type[] {{ typeof(global::System.Runtime.CompilerServices.{callConvType}) }})]");
            lines.Add($"[global::System.Runtime.InteropServices.LibraryImport(\"{info.LibraryPath}\", EntryPoint = \"{info.EntryPoint}\"{stringMarshalSuffix})]");
        }
        else
        {
            lines.Add($"[UnmanagedCallConv(CallConvs = new Type[] {{ typeof({callConvType}) }})]");
            lines.Add($"[LibraryImport(\"{info.LibraryPath}\", EntryPoint = \"{info.EntryPoint}\"{stringMarshalSuffix})]");
        }

        // Return type (async always returns void)
        var returnTypeStr = info.IsAsync ? "void" : info.ReturnType;

        // Bool return marshalling
        if (MarshallingHelpers.IsBoolType(returnTypeStr))
            lines.Add("[return: MarshalAs(UnmanagedType.U1)]");

        // Build parameter string with metadata parameters
        var paramsStr = info.ParametersString;
        if (info.MetadataParameters != null && info.MetadataParameters.Count > 0)
        {
            var metadataParams = string.Join(", ", info.MetadataParameters);
            if (!string.IsNullOrEmpty(paramsStr))
                paramsStr = $"{paramsStr}, {metadataParams}";
            else
                paramsStr = metadataParams;
        }

        // Build modifiers
        var visibility = info.Visibility switch
        {
            PInvokeVisibility.Private => "private",
            PInvokeVisibility.Internal => "internal",
            PInvokeVisibility.Public => "public",
            _ => "private"
        };

        var newModifier = info.HasNewModifier ? "new " : "";
        var unsafeModifier = info.IsUnsafe ? "unsafe " : "";

        lines.Add($"{visibility} static {newModifier}{unsafeModifier}partial {returnTypeStr} {info.MethodName}({paramsStr});");

        return lines;
    }
}
