// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Foundation.Data ↔ C# byte[].
/// Parameter direction: byte[] → Swift.Foundation.Data.FromByteArray(param).
/// Return direction: Swift.Foundation.Data.ToByteArray() or MarshalFromSwift for indirect.
/// </summary>
public class DataProjection : ITypeProjection
{
    public string PublicType => "byte[]";
    public string PInvokeType => "Swift.Foundation.Data";
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Line($"var {paramName}Swift = Swift.Foundation.Data.FromByteArray({paramName});")
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
                PInvokeExpression = $"{resultName}.ToByteArray()"
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwiftObject<Swift.Foundation.Data>({resultName}).ToByteArray()"
            },
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) => $"Swift.Foundation.Data.FromByteArray({elementVar})";
    public string? GetReturnElementConversion(string elementVar) => $"{elementVar}.ToByteArray()";
    public bool ElementRequiresDisposal => false;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
