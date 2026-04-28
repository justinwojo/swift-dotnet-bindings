// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for blittable types (int, nint, double, float, IntPtr, frozen value-type structs).
/// No marshalling needed — the same type is used in both public and P/Invoke contexts.
/// </summary>
public class BlittableProjection : ITypeProjection
{
    public BlittableProjection(string typeName, bool isGenericParameter = false)
    {
        PublicType = typeName;
        PInvokeType = typeName;
        IsGenericParameter = isGenericParameter;
    }

    public string PublicType { get; }
    public string PInvokeType { get; }

    /// <summary>
    /// True when this projection stands in for an unconstrained generic type parameter
    /// (e.g., TValue in a generic struct). Layout is unknown at compile time, so callers
    /// must avoid emitting inline byte-level reads keyed on Unsafe.SizeOf or
    /// TypeMetadata.Size — the buffer may be exactly Size bytes (class TValue) with no
    /// trailing discriminant. Wrap-and-marshal paths (SwiftOptional&lt;T&gt;) still work.
    /// </summary>
    public bool IsGenericParameter { get; }
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName) => MarshalPlan.PassThrough(paramName);

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy) => MarshalPlan.PassThrough(resultName);

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
