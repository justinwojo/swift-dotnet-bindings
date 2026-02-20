// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Strategy for how a return value is received from Swift.
/// </summary>
public enum ReturnStrategy
{
    /// <summary>The value is returned directly in a register.</summary>
    Direct,

    /// <summary>The value is returned via SwiftIndirectResult (large structs).</summary>
    IndirectResult,

    /// <summary>The value is returned via an out-buffer pointer (large optionals).</summary>
    OutBuffer,

    /// <summary>The value is returned via an async callback.</summary>
    AsyncCallback
}

/// <summary>
/// Defines how a single Swift type is projected to C# for P/Invoke interop.
/// Each projection knows its public C# type, its P/Invoke type, and how to
/// marshal between them in both parameter and return directions.
/// </summary>
public interface ITypeProjection
{
    /// <summary>The C# type seen by the consumer (e.g., "string", "IReadOnlyList&lt;int&gt;").</summary>
    string PublicType { get; }

    /// <summary>The C# type used in the P/Invoke declaration (e.g., "SwiftString", "IntPtr").</summary>
    string PInvokeType { get; }

    /// <summary>Optional attribute for the P/Invoke parameter (e.g., "[MarshalAs(UnmanagedType.U1)]").</summary>
    string? PInvokeAttribute { get; }

    /// <summary>
    /// Produces a plan for marshalling this type as a parameter (C# → Swift direction).
    /// </summary>
    /// <param name="paramName">The C# parameter name.</param>
    /// <returns>A marshal plan with setup, expression, and cleanup.</returns>
    MarshalPlan GetParameterPlan(string paramName);

    /// <summary>
    /// Produces a plan for marshalling this type as a return value (Swift → C# direction).
    /// </summary>
    /// <param name="resultName">The variable name for the raw P/Invoke result.</param>
    /// <param name="strategy">How the return value is received from Swift.</param>
    /// <returns>A marshal plan with setup, expression, and cleanup.</returns>
    MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy);

    /// <summary>Whether this projection requires a Swift wrapper function.</summary>
    bool RequiresSwiftWrapper { get; }

    /// <summary>
    /// Gets the Swift wrapper code if needed, or null if no wrapper is required.
    /// </summary>
    /// <param name="context">Context for generating the Swift wrapper.</param>
    /// <returns>Swift source code for the wrapper, or null.</returns>
    string? GetSwiftWrapperCode(SwiftWrapperContext context);
}

/// <summary>
/// Context information needed to generate a Swift wrapper function.
/// </summary>
public record SwiftWrapperContext
{
    /// <summary>The mangled name of the Swift function being wrapped.</summary>
    public string MangledName { get; init; } = "";

    /// <summary>The module name.</summary>
    public string ModuleName { get; init; } = "";

    /// <summary>The method name.</summary>
    public string MethodName { get; init; } = "";
}
