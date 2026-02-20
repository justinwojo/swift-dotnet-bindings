// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift.Optional&lt;T&gt; ↔ C# T?.
/// Composes with an inner projection for the wrapped type.
///
/// Parameter direction: null-check branching → SwiftOptional.NewSome/NewNone + PayloadBuffer.
/// Return direction: MarshalFromSwift + ToNullable (or discriminant check for existentials/containers).
/// </summary>
public class OptionalProjection : ITypeProjection
{
    private readonly ITypeProjection _innerProjection;
    private readonly bool _isExistentialInner;

    /// <summary>
    /// Creates an optional projection.
    /// </summary>
    /// <param name="innerProjection">The projection for the wrapped type.</param>
    /// <param name="isExistentialInner">Whether the inner type is an existential (uses discriminant check instead of ToNullable).</param>
    public OptionalProjection(ITypeProjection innerProjection, bool isExistentialInner = false)
    {
        _innerProjection = innerProjection;
        _isExistentialInner = isExistentialInner;
    }

    /// <summary>The inner projection for the wrapped type.</summary>
    public ITypeProjection InnerProjection => _innerProjection;

    public string PublicType => $"{_innerProjection.PublicType}?";
    public string PInvokeType => "IntPtr";
    public string? PInvokeAttribute => null;

    /// <summary>
    /// The SwiftOptional type parameter — uses ContainerTypeName for containers (SwiftArray, SwiftDictionary)
    /// and PInvokeType for all others.
    /// </summary>
    private string OptionalTypeParam => _innerProjection.ContainerTypeName;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        var optTypeParam = OptionalTypeParam;
        var innerParamConv = _innerProjection.GetParameterElementConversion($"{paramName}Value");
        var containerPlan = _innerProjection.GetContainerCreationPlan($"{paramName}Value");
        var needsComplexInner = innerParamConv != null || containerPlan != null ||
            _innerProjection.PublicType != _innerProjection.PInvokeType;
        var setup = new List<MarshalStatement>();

        if (needsComplexInner)
        {
            // Complex inner type — multi-statement branching
            setup.Add(new MarshalStatement.Line(
                $"SwiftOptional<{optTypeParam}> {paramName}SwiftInner;"));

            var someBody = new List<MarshalStatement>();
            if (containerPlan != null)
            {
                // Inner is a container (Array, Dictionary) — use container creation plan
                // which creates the SwiftArray/SwiftDictionary without PayloadBuffer extraction
                someBody.AddRange(containerPlan.SetupStatements);
                someBody.Add(new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewSome({containerPlan.PInvokeExpression});"));
            }
            else if (innerParamConv != null)
            {
                // Inner has element conversion (string, enum, etc.) — use it directly
                someBody.Add(new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewSome({innerParamConv});"));
            }
            else
            {
                // Inner has different public/pinvoke types but no conversion/container — passthrough
                someBody.Add(new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewSome({paramName}Value);"));
            }
            setup.Add(new MarshalStatement.Block(
                $"if ({paramName} is {{ }} {paramName}Value)", someBody));

            var noneBody = new List<MarshalStatement>
            {
                new MarshalStatement.Line(
                    $"{paramName}SwiftInner = SwiftOptional<{optTypeParam}>.NewNone();")
            };
            setup.Add(new MarshalStatement.Block("else", noneBody));

            setup.Add(new MarshalStatement.Using(
                $"SwiftOptional<{optTypeParam}>", $"{paramName}Swift", $"{paramName}SwiftInner"));
        }
        else
        {
            // Simple inner type — inline ternary
            setup.Add(new MarshalStatement.Using(
                $"SwiftOptional<{optTypeParam}>", $"{paramName}Swift",
                $"{paramName} is {{ }} {paramName}Value ? SwiftOptional<{optTypeParam}>.NewSome({paramName}Value) : SwiftOptional<{optTypeParam}>.NewNone()"));
        }

        setup.Add(new MarshalStatement.Using(
            "PayloadBuffer<IntPtr>", $"{paramName}Disposable", $"{paramName}Swift.PayloadBuffer"));
        setup.Add(new MarshalStatement.Line(
            $"IntPtr {paramName}Buf = {paramName}Disposable.Buffer;"));

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"{paramName}Buf"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        var optTypeParam = OptionalTypeParam;

        if (_isExistentialInner)
        {
            // Existential inner — discriminant check + proxy construction
            var elemConversion = _innerProjection.GetReturnElementConversion("swiftResult.Some");
            var convExpr = elemConversion ?? "swiftResult.Some";
            return BuildDiscriminantReturnPlan(resultName, strategy, optTypeParam, convExpr);
        }

        // Container inner (Array, Dictionary) — discriminant check + container conversion
        var containerConv = _innerProjection.GetReturnContainerConversion("swiftResult.Some");
        if (containerConv != null)
        {
            return BuildDiscriminantReturnPlan(resultName, strategy, optTypeParam, containerConv);
        }

        // Non-existential, non-container — ToNullable() path
        var marshalFromSwift = $"SwiftMarshal.MarshalFromSwift<SwiftOptional<{optTypeParam}>>";
        var innerRetConv = _innerProjection.GetReturnElementConversion("rawVal");

        if (innerRetConv != null)
        {
            // Element conversion needed: MarshalFromSwift → ToNullable() → conditional convert
            return strategy switch
            {
                ReturnStrategy.Direct => new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"var rawOpt = {marshalFromSwift}(new IntPtr(&{resultName})).ToNullable();")
                    },
                    PInvokeExpression = $"rawOpt is {{ }} rawVal ? {innerRetConv} : null",
                    RequiresUnsafe = true
                },
                ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
                {
                    SetupStatements = new List<MarshalStatement>
                    {
                        new MarshalStatement.Line(
                            $"var rawOpt = {marshalFromSwift}({resultName}).ToNullable();")
                    },
                    PInvokeExpression = $"rawOpt is {{ }} rawVal ? {innerRetConv} : null"
                },
                ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
                _ => MarshalPlan.PassThrough(resultName)
            };
        }

        // No element conversion — simple ToNullable()
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                PInvokeExpression = $"{marshalFromSwift}(new IntPtr(&{resultName})).ToNullable()",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
            {
                PInvokeExpression = $"{marshalFromSwift}({resultName}).ToNullable()"
            },
            ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    /// <summary>
    /// Builds a return plan using discriminant check (Case == None ? null : conversion).
    /// Used for both existential and container inners.
    /// </summary>
    private static MarshalPlan BuildDiscriminantReturnPlan(
        string resultName, ReturnStrategy strategy, string optTypeParam, string convExpr)
    {
        var marshalFromSwift = $"SwiftMarshal.MarshalFromSwift<SwiftOptional<{optTypeParam}>>";
        return strategy switch
        {
            ReturnStrategy.Direct => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var swiftResult = {marshalFromSwift}(new IntPtr(&{resultName}));")
                },
                PInvokeExpression = $"swiftResult.Case == SwiftOptionalCases.None ? null : {convExpr}",
                RequiresUnsafe = true
            },
            ReturnStrategy.IndirectResult or ReturnStrategy.OutBuffer => new MarshalPlan
            {
                SetupStatements = new List<MarshalStatement>
                {
                    new MarshalStatement.Line(
                        $"var swiftResult = {marshalFromSwift}({resultName});")
                },
                PInvokeExpression = $"swiftResult.Case == SwiftOptionalCases.None ? null : {convExpr}"
            },
            ReturnStrategy.AsyncCallback => MarshalPlan.PassThrough(resultName),
            _ => MarshalPlan.PassThrough(resultName)
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;
}
