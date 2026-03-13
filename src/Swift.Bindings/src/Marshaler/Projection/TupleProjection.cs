// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Projection for Swift tuples ↔ C# ValueTuple.
/// Composes with per-element projections for element-wise marshalling.
///
/// Parameter direction: per-element conversion from public types to P/Invoke types.
/// Return direction: per-element MarshalFromSwift + conversion from result.Item{N}.
/// </summary>
public class TupleProjection : ITypeProjection
{
    private readonly IReadOnlyList<ITypeProjection> _elementProjections;

    public TupleProjection(IReadOnlyList<ITypeProjection> elementProjections)
    {
        _elementProjections = elementProjections;
    }

    /// <summary>The per-element projections.</summary>
    public IReadOnlyList<ITypeProjection> ElementProjections => _elementProjections;

    public string PublicType
    {
        get
        {
            var types = _elementProjections.Select(p => p.PublicType);
            return $"({string.Join(", ", types)})";
        }
    }

    public string PInvokeType
    {
        get
        {
            var types = _elementProjections.Select(p => p.PInvokeType);
            return $"ValueTuple<{string.Join(", ", types)}>";
        }
    }

    public string? PInvokeAttribute => null;

    public MarshalPlan GetParameterPlan(string paramName)
    {
        var needsConversion = _elementProjections.Any(p => p.GetParameterElementConversion("x") != null);
        if (!needsConversion)
            return MarshalPlan.PassThrough(paramName);

        // Per-element conversion
        var setup = new List<MarshalStatement>();
        var elemExprs = new List<string>();

        for (int i = 0; i < _elementProjections.Count; i++)
        {
            var proj = _elementProjections[i];
            var itemAccess = $"{paramName}.Item{i + 1}";
            var conv = proj.GetParameterElementConversion(itemAccess);
            if (conv != null)
            {
                var elemVar = $"{paramName}Elem{i}";
                setup.Add(new MarshalStatement.Line($"var {elemVar} = {conv};"));
                elemExprs.Add(elemVar);
            }
            else
            {
                elemExprs.Add(itemAccess);
            }
        }

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"({string.Join(", ", elemExprs)})"
        };
    }

    public MarshalPlan GetReturnPlan(string resultName, ReturnStrategy strategy)
    {
        var needsConversion = _elementProjections.Any(p => p.GetReturnElementConversion("x") != null);

        if (!needsConversion)
        {
            return strategy switch
            {
                ReturnStrategy.Direct => MarshalPlan.PassThrough(resultName),
                _ => MarshalPlan.PassThrough(resultName)
            };
        }

        // Per-element marshalling from result items
        var setup = new List<MarshalStatement>();
        var elemExprs = new List<string>();
        var requiresUnsafe = false;

        for (int i = 0; i < _elementProjections.Count; i++)
        {
            var proj = _elementProjections[i];
            var itemAccess = strategy == ReturnStrategy.Direct
                ? $"{resultName}.Item{i + 1}"
                : $"{resultName}.Item{i + 1}";
            var conv = proj.GetReturnElementConversion(itemAccess);

            if (conv != null)
            {
                var elemVar = $"elem{i}";
                setup.Add(new MarshalStatement.Line($"var {elemVar} = {conv};"));
                elemExprs.Add(elemVar);
            }
            else
            {
                elemExprs.Add(itemAccess);
            }
        }

        return new MarshalPlan
        {
            SetupStatements = setup,
            PInvokeExpression = $"({string.Join(", ", elemExprs)})",
            RequiresUnsafe = requiresUnsafe
        };
    }

    public bool RequiresSwiftWrapper => false;
    public string? GetSwiftWrapperCode(SwiftWrapperContext context) => null;

    public T Accept<T>(IProjectionVisitor<T> visitor) => visitor.Visit(this);
}
