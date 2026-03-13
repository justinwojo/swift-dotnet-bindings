// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Array&lt;T&gt; ↔ C# IReadOnlyList&lt;T&gt; (return) or IEnumerable&lt;T&gt; (parameter).
/// Composes with an inner element projection for element-wise marshalling.
///
/// Parameter direction: FromEnumerable + PayloadBuffer, with optional element conversion + disposal.
/// Return direction: MarshalFromSwift + AsProjected with element conversion lambda.
/// </summary>
public class ArrayProjection : ITypeProjection
{
    private readonly ITypeProjection _elementProjection;
    private readonly bool _isParameter;

    public ArrayProjection(ITypeProjection elementProjection, bool isParameter)
    {
        _elementProjection = elementProjection;
        _isParameter = isParameter;
    }

    /// <summary>The inner element projection for composition.</summary>
    public ITypeProjection ElementProjection => _elementProjection;

    public string PublicType => _isParameter
        ? $"IEnumerable<{_elementProjection.PublicType}>"
        : $"IReadOnlyList<{_elementProjection.PublicType}>";

    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    public string SwiftContainerGenericType => $"SwiftArray<{_elementProjection.SwiftContainerGenericType}>";

    public string ContainerTypeName => $"SwiftArray<{_elementProjection.MarshalFromSwiftType}>";

    /// <summary>
    /// For MarshalFromSwift in return direction, use MarshalFromSwiftType of inner elements
    /// (same as ContainerTypeName). This ensures OptionalProjection wrapping an ArrayProjection
    /// gets the public type names (e.g., SwiftArray&lt;STPPaymentMethod&gt;) not P/Invoke types.
    /// </summary>
    public string MarshalFromSwiftType => ContainerTypeName;

    /// <summary>
    /// Builds the container creation statements (element conversion + SwiftArray.FromEnumerable)
    /// without PayloadBuffer extraction. Returns setup statements and the container variable name.
    /// </summary>
    private (List<MarshalStatement> setup, string containerExpr) BuildContainerSetup(string paramName)
    {
        var rawElem = _elementProjection.SwiftContainerGenericType;
        var elemConversion = _elementProjection.GetParameterElementConversion("e");
        var needsConversion = elemConversion != null;
        var setup = new List<MarshalStatement>();

        if (needsConversion && _elementProjection.ElementRequiresDisposal)
        {
            // Materialize to list for disposal: .ToList() + try/finally + SwiftInner intermediate
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Converted = {paramName}.Select(e => {elemConversion}).ToList();"));
            setup.Add(new MarshalStatement.Line(
                $"SwiftArray<{rawElem}> {paramName}SwiftInner;"));

            var tryBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftArray<{rawElem}>.FromEnumerable({paramName}Converted);")
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
        else if (needsConversion)
        {
            // Conversion needed but no disposal — lazy Select without materialization
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Containers = {paramName}.Select(e => {elemConversion});"));
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftArray<{rawElem}>.FromEnumerable({paramName}Containers);"));
            return (setup, $"{paramName}SwiftDirect");
        }
        else
        {
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftArray<{rawElem}>.FromEnumerable({paramName});"));
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
        var selector = elemConversion != null
            ? $"e => {elemConversion}"
            : "e => e";
        return $"{containerVar}.AsProjected({selector})";
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Use MarshalFromSwiftType for return — classes/non-frozen structs need the real type name
        // (not IntPtr) for MarshalFromSwift to construct instances via ISwiftObject.NewFromPayload.
        var rawElem = _elementProjection.MarshalFromSwiftType;
        var elemConversion = _elementProjection.GetReturnElementConversion("e");

        var asProjected = elemConversion != null
            ? $".AsProjected(e => {elemConversion})"
            : $".AsProjected(e => e)";

        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftArray<{rawElem}>>(new IntPtr(&{resultName})){asProjected}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftArray<{rawElem}>>({resultName}){asProjected}"
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftArray<{rawElem}>>({resultName}){asProjected}"
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
            return $"SwiftArray<{rawElem}>.FromEnumerable({elementVar}.Select(e => {elemConversion}))";
        return $"SwiftArray<{rawElem}>.FromEnumerable({elementVar})";
    }

    public string? GetReturnElementConversion(string elementVar)
    {
        var elemConversion = _elementProjection.GetReturnElementConversion("e");
        var selector = elemConversion != null ? $"e => {elemConversion}" : "e => e";
        return $"{elementVar}.AsProjected({selector})";
    }

    public bool ElementRequiresDisposal => true;

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
