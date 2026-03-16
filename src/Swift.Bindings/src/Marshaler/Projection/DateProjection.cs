// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Foundation.Date ↔ C# DateTimeOffset.
/// Swift's Date is a frozen struct wrapping a single Double (TimeInterval = seconds since
/// Jan 1, 2001 00:00:00 UTC). Its C ABI is just a Double (8 bytes in FP register).
/// C#'s DateTimeOffset is 12 bytes — passing it as blittable causes ABI mismatch on NativeAOT.
/// This projection marshals DateTimeOffset ↔ double at the P/Invoke boundary.
/// </summary>
public class DateProjection : ITypeProjection
{
    /// <summary>Swift's reference epoch: Jan 1, 2001 00:00:00 UTC.</summary>
    internal const string SwiftEpoch = "new System.DateTimeOffset(2001, 1, 1, 0, 0, 0, System.TimeSpan.Zero)";

    public string PublicType => "System.DateTimeOffset";
    public string PInvokeType => "double";
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line($"var {paramName}Swift = ({paramName} - {SwiftEpoch}).TotalSeconds;")
            },
            PInvokeExpression = $"{paramName}Swift"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"{SwiftEpoch}.AddSeconds({resultName})"
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"{SwiftEpoch}.AddSeconds(System.Runtime.InteropServices.Marshal.PtrToStructure<double>({resultName}))"
            },
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) =>
        $"({elementVar} - {SwiftEpoch}).TotalSeconds";
    public string? GetReturnElementConversion(string elementVar) =>
        $"{SwiftEpoch}.AddSeconds({elementVar})";
    public bool ElementRequiresDisposal => false;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
