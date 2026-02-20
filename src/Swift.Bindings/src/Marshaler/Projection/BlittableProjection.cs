// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for blittable types (int, nint, double, float, IntPtr, frozen value-type structs).
/// No marshalling needed — the same type is used in both public and P/Invoke contexts.
/// </summary>
public class BlittableProjection : ITypeProjection
{
    public BlittableProjection(string typeName)
    {
        PublicType = typeName;
        PInvokeType = typeName;
    }

    public string PublicType { get; }
    public string PInvokeType { get; }
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName) => MarshalPlan.PassThrough(paramName);

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy) => MarshalPlan.PassThrough(resultName);

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;
}
