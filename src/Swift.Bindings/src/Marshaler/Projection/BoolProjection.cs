// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Bool → C# bool.
/// Requires [MarshalAs(UnmanagedType.U1)] for P/Invoke with DisableRuntimeMarshalling.
/// </summary>
public class BoolProjection : ITypeProjection
{
    public string PublicType => "bool";
    public string PInvokeType => "bool";
    public string? PInvokeAttribute => "[MarshalAs(UnmanagedType.U1)]";

    public MarshalPlan GetParameterPlan(string paramName) => MarshalPlan.PassThrough(paramName);

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy) => MarshalPlan.PassThrough(resultName);

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
