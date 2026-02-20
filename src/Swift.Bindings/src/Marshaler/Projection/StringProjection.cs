// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.String ↔ C# string.
/// Parameter direction: string → new SwiftString(param) with disposal.
/// Return direction: SwiftString.ToString() or MarshalFromSwift for indirect.
/// </summary>
public class StringProjection : ITypeProjection
{
    public string PublicType => "string";
    public string PInvokeType => "SwiftString";
    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        return new MarshalPlan
        {
            SetupStatements = new List<MarshalStatement>
            {
                new MarshalStatement.Using("SwiftString", $"{paramName}Swift", $"new SwiftString({paramName})")
            },
            PInvokeExpression = $"{paramName}Swift",
            UsingDeclarations = new List<(string, string)>
            {
                ("SwiftString", $"{paramName}Swift")
            }
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"{resultName}.ToString()"
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftString.MarshalFromSwift({resultName})"
            },
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public string? GetParameterElementConversion(string elementVar) => $"new SwiftString({elementVar})";
    public string? GetReturnElementConversion(string elementVar) => $"{elementVar}.ToString()";
    public bool ElementRequiresDisposal => true;
}
