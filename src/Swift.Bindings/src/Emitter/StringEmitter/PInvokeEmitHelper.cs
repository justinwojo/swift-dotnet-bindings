// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Calling convention for P/Invoke declarations.
/// Most methods use CallConvCdecl (routed through @_cdecl wrappers or native ARM64 thunks).
/// Methods with WrapperStrategy.None that target raw Swift symbols use CallConvSwift.
/// </summary>
public enum PInvokeCallingConvention
{
    /// <summary>C calling convention via CallConvCdecl. Used for @_cdecl wrappers and native thunks.</summary>
    Cdecl,

    /// <summary>Swift calling convention via CallConvSwift. Used for direct Swift symbol calls
    /// (WrapperStrategy.None) where no wrapper or thunk is available.</summary>
    Swift
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

    /// <summary>Calling convention: Cdecl for wrappers/thunks, Swift for direct symbol calls.</summary>
    public PInvokeCallingConvention CallingConvention { get; init; } = PInvokeCallingConvention.Cdecl;

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

    /// <summary>
    /// Optional handle to the per-module emission context. When supplied alongside
    /// <see cref="EnforceWrapperContract"/>, <see cref="PInvokeEmitHelper"/> consults
    /// <see cref="ModuleEmissionContext.IsWrapperSymbolRegistered"/> before emitting
    /// — refusing (via <see cref="WrapperSymbolContractException"/>) when the entry
    /// point matches the wrapper-symbol convention but no wrapper-emit path
    /// registered it. Required for the in-band wrapper-symbol contract check.
    /// </summary>
    public ModuleEmissionContext? EmissionContext { get; init; }

    /// <summary>
    /// When true and <see cref="EmissionContext"/> is non-null, the in-band
    /// wrapper-symbol contract check fires. Default is false so existing call
    /// sites preserve their current behaviour while the contract is rolled out
    /// chokepoint-by-chokepoint.
    /// </summary>
    public bool EnforceWrapperContract { get; init; }
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
    /// Returns true when <paramref name="entryPoint"/> follows the wrapper-symbol
    /// naming convention (SBW_… prefix). Wrapper-emit owns this prefix; binding-emit
    /// only ever produces it when it expects to call into a wrapper that wrapper-emit
    /// emitted. Direct Swift mangled symbols ($s…) and ObjC selectors are excluded.
    /// </summary>
    public static bool IsWrapperEntryPoint(string entryPoint) =>
        !string.IsNullOrEmpty(entryPoint) && entryPoint.StartsWith("SBW_", StringComparison.Ordinal);

    /// <summary>
    /// Format a P/Invoke declaration as individual lines (unindented).
    /// Callers prepend their own indentation when appending to StringBuilder or raw strings.
    /// </summary>
    /// <exception cref="WrapperSymbolContractException">
    /// Thrown when <see cref="PInvokeEmissionInfo.EnforceWrapperContract"/> is true,
    /// <see cref="PInvokeEmissionInfo.EmissionContext"/> is non-null, the calling
    /// convention is <see cref="PInvokeCallingConvention.Cdecl"/>, the entry point
    /// matches the wrapper-symbol convention, and the symbol was never registered.
    /// </exception>
    public static IReadOnlyList<string> FormatDeclarationLines(PInvokeEmissionInfo info)
    {
        // In-band wrapper-symbol contract: refuse to emit a P/Invoke whose entry
        // point looks like a wrapper symbol (SBW_…) when wrapper-emit never
        // registered it. Catches the failure shape behind the three 0.10.0 bugs
        // where binding-emit referenced a symbol that wrapper-emit never produced.
        if (info.EnforceWrapperContract &&
            info.EmissionContext != null &&
            info.CallingConvention == PInvokeCallingConvention.Cdecl &&
            IsWrapperEntryPoint(info.EntryPoint) &&
            !info.EmissionContext.IsWrapperSymbolRegistered(info.EntryPoint))
        {
            throw new WrapperSymbolContractException(info.EntryPoint, info.MethodName);
        }

        var lines = new List<string>();

        // Calling convention attribute — use the specified convention
        var callConvType = info.CallingConvention switch
        {
            PInvokeCallingConvention.Swift => "CallConvSwift",
            _ => "CallConvCdecl"
        };

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
