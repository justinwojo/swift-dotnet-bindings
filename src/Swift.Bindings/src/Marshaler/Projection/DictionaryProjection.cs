// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Dictionary&lt;K,V&gt; ↔ C# IReadOnlyDictionary (return) or IDictionary (parameter).
/// Composes with inner key and value projections for element-wise marshalling.
///
/// Parameter direction: FromDictionary + PayloadBuffer, with optional key/value conversion + disposal.
/// Return direction: MarshalFromSwift + AsProjected with key/value conversion lambdas.
/// </summary>
public class DictionaryProjection : ITypeProjection
{
    private readonly ITypeProjection _keyProjection;
    private readonly ITypeProjection _valueProjection;
    private readonly bool _isParameter;

    public DictionaryProjection(ITypeProjection keyProjection, ITypeProjection valueProjection, bool isParameter)
    {
        _keyProjection = keyProjection;
        _valueProjection = valueProjection;
        _isParameter = isParameter;
    }

    /// <summary>The inner key projection.</summary>
    public ITypeProjection KeyProjection => _keyProjection;

    /// <summary>The inner value projection.</summary>
    public ITypeProjection ValueProjection => _valueProjection;

    public string PublicType => _isParameter
        ? $"IDictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>"
        : $"IReadOnlyDictionary<{_keyProjection.PublicType}, {_valueProjection.PublicType}>";

    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    public string SwiftContainerGenericType => ContainerTypeName;

    public string ContainerTypeName => $"SwiftDictionary<{_keyProjection.SwiftContainerGenericType}, {_valueProjection.SwiftContainerGenericType}>";

    /// <summary>
    /// Builds the container creation statements (key/value conversion + SwiftDictionary.FromDictionary)
    /// without PayloadBuffer extraction.
    /// </summary>
    private (List<MarshalStatement> setup, string containerExpr) BuildContainerSetup(string paramName)
    {
        var rawK = _keyProjection.SwiftContainerGenericType;
        var rawV = _valueProjection.SwiftContainerGenericType;
        var keyConv = _keyProjection.GetParameterElementConversion("kvp.Key");
        var valConv = _valueProjection.GetParameterElementConversion("kvp.Value");
        var needsConversion = keyConv != null || valConv != null;
        var setup = new List<MarshalStatement>();

        if (needsConversion)
        {
            var keyExpr = keyConv ?? "kvp.Key";
            var valExpr = valConv ?? "kvp.Value";

            setup.Add(new MarshalStatement.Line(
                $"var {paramName}Converted = {paramName}.Select(kvp => new KeyValuePair<{rawK}, {rawV}>({keyExpr}, {valExpr})).ToList();"));
            setup.Add(new MarshalStatement.Line(
                $"SwiftDictionary<{rawK}, {rawV}> {paramName}SwiftInner;"));

            var tryBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftDictionary<{rawK}, {rawV}>.FromDictionary({paramName}Converted);")
            };

            var finallyBody = new List<MarshalStatement>();
            if (_keyProjection.ElementRequiresDisposal)
            {
                finallyBody.Add(new MarshalStatement.Line(
                    $"foreach (var _item in {paramName}Converted) _item.Key.Dispose();"));
            }
            if (_valueProjection.ElementRequiresDisposal)
            {
                finallyBody.Add(new MarshalStatement.Line(
                    $"foreach (var _item in {paramName}Converted) _item.Value.Dispose();"));
            }

            if (finallyBody.Count > 0)
            {
                setup.Add(new MarshalStatement.Block("try", tryBody));
                setup.Add(new MarshalStatement.Block("finally", finallyBody));
            }
            else
            {
                setup.AddRange(tryBody);
            }

            return (setup, $"{paramName}SwiftInner");
        }
        else
        {
            setup.Add(new MarshalStatement.Line(
                $"var {paramName}SwiftDirect = SwiftDictionary<{rawK}, {rawV}>.FromDictionary({paramName});"));
            return (setup, $"{paramName}SwiftDirect");
        }
    }

    public MarshalPlan GetParameterPlan(string paramName)
    {
        var (setup, containerExpr) = BuildContainerSetup(paramName);

        setup.Add(new MarshalStatement.Using(
            ContainerTypeName, $"{paramName}Swift", containerExpr));
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
        var keyConv = _keyProjection.GetReturnElementConversion("k");
        var valConv = _valueProjection.GetReturnElementConversion("v");
        return $"{containerVar}{BuildAsProjected(keyConv, valConv)}";
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        // Use MarshalFromSwiftType for return — classes/non-frozen structs need the real type name
        var rawK = _keyProjection.MarshalFromSwiftType;
        var rawV = _valueProjection.MarshalFromSwiftType;
        var keyConv = _keyProjection.GetReturnElementConversion("k");
        var valConv = _valueProjection.GetReturnElementConversion("v");

        var asProjected = BuildAsProjected(keyConv, valConv);

        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftDictionary<{rawK}, {rawV}>>(new IntPtr(&{resultName})){asProjected}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftDictionary<{rawK}, {rawV}>>({resultName}){asProjected}"
            },
            ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"SwiftMarshal.MarshalFromSwift<SwiftDictionary<{rawK}, {rawV}>>({resultName}){asProjected}"
            },
            ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    /// <summary>
    /// Builds the AsProjected call matching SwiftDictionary runtime API overloads:
    /// - Value-only: AsProjected(Func&lt;TValue,TResult&gt; valueSelector)
    /// - Key+value: AsProjected(Func&lt;TKey,TResultKey&gt; keySelector, Func&lt;TResultKey,TKey&gt; reverseKeySelector, Func&lt;TValue,TResultValue&gt; valueSelector)
    /// </summary>
    private string BuildAsProjected(string? keyConv, string? valConv)
    {
        if (keyConv != null)
        {
            var reverseKeyConv = _keyProjection.GetParameterElementConversion("k") ?? "k";
            var valSelector = valConv != null ? $"v => {valConv}" : "v => v";
            return $".AsProjected(k => {keyConv}, k => {reverseKeyConv}, {valSelector})";
        }
        if (valConv != null)
        {
            return $".AsProjected(v => {valConv})";
        }
        return ".AsProjected(v => v)";
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;
}
