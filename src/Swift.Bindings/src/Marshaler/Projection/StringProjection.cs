// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.String ↔ C# string.
/// Parameter direction: string → new SwiftString(param) with disposal.
/// Return direction: SwiftString.ToString() or MarshalFromSwift for indirect.
/// </summary>
public class StringProjection : ITypeProjection, IResolvedReturnLocalProjection
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
                new MarshalStatement.Using("SwiftString", $"{paramName}Swift", $"new SwiftString({paramName})"),
                new MarshalStatement.Using("PayloadBuffer<SwiftString.Buffer>", $"{paramName}Disposable",
                    $"{paramName}Swift.PayloadBuffer")
            },
            PInvokeExpression = $"{paramName}Disposable.Buffer",
            // A Swift String longer than the inline small-string form keeps its bytes on a
            // refcounted storage object, so passing the lowered buffer to a consuming callee
            // borrows a count the transient SwiftString still owns and will release.
            OwnedValueArgument = new("SwiftString", $"{paramName}Swift.Payload")
        };
    }

    /// <summary>
    /// The body local the direct arm declares when no caller-resolved name is supplied. It is not
    /// derived from the name handed in, so a member whose own parameter is spelled this way has to
    /// pass a resolved name instead.
    /// </summary>
    internal const string DefaultLocalName = "swiftResult";

    /// <inheritdoc />
    public string DefaultReturnLocalName => DefaultLocalName;

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
        => GetReturnPlan(resultName, strategy, DefaultLocalName);

    /// <summary>
    /// Builds the return plan, declaring the direct arm's read-back local as
    /// <paramref name="localName"/>.
    /// </summary>
    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy, string localName)
    {
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var {localName} = SwiftMarshal.MarshalFromSwiftObject<SwiftString>(new IntPtr(&{resultName}));")
                },
                PInvokeExpression = $"{localName}.ToString()",
                RequiresUnsafe = true
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

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
