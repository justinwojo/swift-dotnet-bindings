// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Set&lt;T&gt; ↔ C# IReadOnlySet&lt;T&gt; (return) or IEnumerable&lt;T&gt; (parameter).
/// Composes with an inner element projection for element-wise marshalling.
///
/// Parameter direction: FromEnumerable + PayloadBuffer, with optional element conversion + disposal.
/// Return direction: MarshalFromSwift + ToHashSet with element conversion lambda.
/// </summary>
public class SetProjection : ITypeProjection
{
    private readonly ITypeProjection _elementProjection;
    private readonly bool _isParameter;

    public SetProjection(ITypeProjection elementProjection, bool isParameter)
    {
        _elementProjection = elementProjection;
        _isParameter = isParameter;
    }

    public ITypeProjection ElementProjection => _elementProjection;

    public string PublicType => _isParameter
        ? $"IEnumerable<{_elementProjection.PublicType}>"
        : $"IReadOnlySet<{_elementProjection.PublicType}>";

    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    public string SwiftContainerGenericType => $"SwiftSet<{_elementProjection.SwiftContainerGenericType}>";

    public string ContainerTypeName => $"SwiftSet<{_elementProjection.MarshalFromSwiftType}>";

    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// Builds the container creation statements (element conversion + SwiftSet.FromEnumerable)
    /// without PayloadBuffer extraction. Returns setup statements and the container variable name.
    /// </summary>
    private (List<MarshalStatement> setup, string containerExpr) BuildContainerSetup(string paramName)
    {
        var rawElem = _elementProjection.SwiftContainerGenericType;
        var elemConversion = _elementProjection.GetParameterElementConversion("e");
        var setup = new List<MarshalStatement>();

        if (elemConversion != null && _elementProjection.ElementRequiresDisposal)
        {
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Converted = {paramName}.Select(e => {elemConversion}).ToList();"));
            setup.Add(new MarshalStatement.Line(
                $"SwiftSet<{rawElem}> {paramName}SwiftInner;"));

            var tryBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftSet<{rawElem}>.FromEnumerable({paramName}Converted);")
            };
            var finallyBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"foreach (var _item in {paramName}Converted) _item.Dispose();")
            };
            setup.Add(new MarshalStatement.Block("try", tryBody));
            setup.Add(new MarshalStatement.Block("finally", finallyBody));

            return (setup, $"{paramName}SwiftInner");
        }
        else if (elemConversion != null)
        {
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Containers = {paramName}.Select(e => {elemConversion});"));
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftSet<{rawElem}>.FromEnumerable({paramName}Containers);"));
            return (setup, $"{paramName}SwiftDirect");
        }
        else
        {
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftSet<{rawElem}>.FromEnumerable({paramName});"));
            return (setup, $"{paramName}SwiftDirect");
        }
    }

    public MarshalPlan GetParameterPlan(string paramName)
    {
        var (setup, containerExpr) = BuildContainerSetup(paramName);

        // Wrap in Using for ownership + PayloadBuffer extraction
        setup.Add(new MarshalStatement.Using(
            SwiftContainerGenericType, $"{paramName}Swift", containerExpr));
        setup.Add(new MarshalStatement.Using(
            "PayloadBuffer<IntPtr>", $"{paramName}Disposable", $"{paramName}Swift.PayloadBuffer"));
        setup.Add(new MarshalStatement.Line(
            $"IntPtr {paramName}Buffer = {paramName}Disposable.Buffer;"));

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"{paramName}Buffer"
        };
    }

    public MarshalPlan? GetContainerCreationPlan(string paramName)
    {
        var (setup, containerExpr) = BuildContainerSetup(paramName);
        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = containerExpr
        };
    }

    public string? GetReturnContainerConversion(string containerVar)
    {
        var elemConversion = _elementProjection.GetReturnElementConversion("e");
        if (elemConversion != null)
            return $"{containerVar}.Select(e => {elemConversion}).ToHashSet()";
        // SwiftSet<T> already implements IReadOnlySet<T>, no conversion needed
        return null;
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        var rawElem = _elementProjection.MarshalFromSwiftType;
        var elemConversion = _elementProjection.GetReturnElementConversion("e");

        // If element conversion is needed (e.g., SwiftString→string), materialize via ToHashSet
        var conversion = elemConversion != null
            ? $".Select(e => {elemConversion}).ToHashSet()"
            : "";

        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftSet<{rawElem}>>(new IntPtr(&{resultName})){conversion}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftSet<{rawElem}>>({resultName}){conversion}"
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftSet<{rawElem}>>({resultName}){conversion}"
            },
            ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public string? GetParameterElementConversion(string elementVar)
    {
        var rawElem = _elementProjection.SwiftContainerGenericType;
        var elemConversion = _elementProjection.GetParameterElementConversion("e");
        if (elemConversion != null)
            return $"SwiftSet<{rawElem}>.FromEnumerable({elementVar}.Select(e => {elemConversion}))";
        return $"SwiftSet<{rawElem}>.FromEnumerable({elementVar})";
    }

    public string? GetReturnElementConversion(string elementVar)
    {
        var elemConversion = _elementProjection.GetReturnElementConversion("e");
        if (elemConversion != null)
            return $"{elementVar}.Select(e => {elemConversion}).ToHashSet()";
        return null;
    }

    public bool ElementRequiresDisposal => true;

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
